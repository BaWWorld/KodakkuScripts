using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using KodakkuAssist.Script;
using KodakkuAssist.Module.GameEvent;
using KodakkuAssist.Module.Draw;

[ScriptType(name: "糖豆人第二关画图", territorys: [1165], guid: "3a9a6d4c-5f3c-4d6d-9f0d-100000000011", version: "0.6.0.0", author: "MYY", note: "糖豆人第二关画图，包含搬水晶相关危险区预测和提丰老师吹飞预警，金蝶跑圈仅做了横杆预测。纯AI编写，反馈请到GITHUB或者你能找到我并私聊我。", updateInfo: "尝试修复漏画不画事件")]
public sealed class VFALLGUYS_Second_AOE : IDisposable
{
    private const string Prefix="VF2AOE_";
    // 34774: 糖果串机关；34799: 拳头机关；34800: 提丰老师；34716: 斧头机关。
    private readonly object gate=new();
    private readonly Dictionary<(uint Action,string Source),Hazard> hazards=new(24);
    private readonly List<(uint Action,string Source)> expired=new(24);
    private readonly BarState[] bars=[new(),new()];
    private ScriptAccessory? accessory;
    private string? frame;
    private DateTime textAt;
    private DateTime cleanupAt;
    private Hazard? breath;
    private bool active;
    private CancellationTokenSource? barPending;
    private const float HazardPreviewSeconds = 1.5f;
    private sealed class Hazard
    {
        public uint Action;
        public string DrawName="";
        public Vector3 Position, End;
        public Vector3 Anchor;
        public float Rotation;
        public DateTime Next, EventAt;
        public DateTime Observed;
        public double Cycle, ForwardCycle, ReverseCycle;
        public int Phase, BreathSamples;
        public bool Warned;
    }
    private sealed class BarState
    {
        public DateTime CastAt, EffectAt, VisibleUntil;
        public int Generation;
        public bool Pending;
        public nint Handle;
    }
    // 仅用于识别空间槽及给首次同步提供周期；绘图始终使用事件实际坐标。
    // 34774 的记录周期是同方向完整往返，相邻反向间隔为其一半。
    // 来源：Mr.Bean3 Runtime/Data/crystal-profiles.json；本地150954采集记录已确认往返。
    private readonly record struct Timing(uint Action,Vector2 A,Vector2 B,double Forward,double Reverse);
    private static readonly Timing[] Timings=
    [
        new(34774,new(-221.0575f,269.9474f),new(-226.1234f,261.1276f),4.00325,3.99575),
        new(34774,new(-207.2023f,261.9211f),new(-212.2377f,253.1624f),2.9855,2.9895),
        new(34774,new(-188.5252f,260.3647f),new(-171.1910f,270.3441f),4.00325,3.99575),
        new(34774,new(-169.2683f,264.5457f),new(-184.5274f,255.7259f),4.00325,3.99575),
        new(34774,new(-176.5621f,259.6628f),new(-181.6587f,268.4825f),2.9865,2.9895),
        new(34799,new(-204,216.5f),new(-204,216.5f),3.6945,3.6945),
        new(34799,new(-228.5549f,263.9007f),new(-228.5549f,263.9007f),4.1035,4.1035),
        new(34799,new(-224.5611f,270.8495f),new(-224.5611f,270.8495f),4.1035,4.1035),
        new(34799,new(-212.7252f,259.3982f),new(-212.7252f,259.3982f),4.1035,4.1035),
        new(34799,new(-196,216.5f),new(-196,216.5f),3.705,3.705),
        new(34774,new(-218.4329f,268.4520f),new(-223.5294f,259.6323f),3.9925,3.9905),
        new(34774,new(-188.9830f,263.5691f),new(-173.7545f,272.3583f),3.99775,3.9925),
        new(34774,new(-169.6956f,267.7501f),new(-186.9993f,257.7707f),3.99775,3.999),
        new(34799,new(-226.5981f,267.3865f),new(-226.5981f,267.3865f),4.109,4.109),
        new(34799,new(-214.6819f,255.9124f),new(-214.6819f,255.9124f),4.109,4.109),
        new(34799,new(-210.6881f,262.8611f),new(-210.6881f,262.8611f),4.109,4.109)
    ];
    [UserSetting("提丰老师吹气提前提醒开关")] public bool EnableBreathReminder {get;set;}=true;
    [UserSetting("吹气前X秒提醒（0.1至5秒）")] public float BreathWarningSeconds {get;set;}=2;
    public void Init(ScriptAccessory sa)
    {
        Dispose();
        lock(gate) { barPending=new(); accessory=sa; active=true; textAt=default; cleanupAt=default; frame=sa.Method.RegistFrameworkUpdateAction(Update,true); }
    }
    public void Dispose()
    {
        lock(gate)
        {
            active=false;
            barPending?.Cancel();
            barPending?.Dispose(); barPending=null;
            foreach(var bar in bars)
            {
                ++bar.Generation; bar.Pending=false;
                if(bar.Handle!=0 && bar.VisibleUntil>DateTime.Now)
                    accessory?.Method.VfxMethod.RemoveVfx(bar.Handle,VfxType.Omen);
                bar.Handle=0; bar.VisibleUntil=bar.CastAt=bar.EffectAt=default;
            }
            if(frame!=null) accessory?.Method.UnregistFrameworkUpdateAction(frame);
            accessory?.Method.ClearFrameworkUpdateAction(this);
            accessory?.Method.RemoveDraw("^"+Prefix);
            frame=null; hazards.Clear(); expired.Clear(); breath=null;
        }
    }
    [ScriptMethod(name:"金蝶跑圈横杆预测",eventType:EventTypeEnum.ActionEffect,
        eventCondition:["ActionId:regex:^(34797|34798)$"],userControl:true)]
    public void PredictRunBar(Event e,ScriptAccessory sa)=>ObserveBar(e,sa,false);
    // 两个入口使用同一方法显示名：KDY 按脚本 GUID + 显示名生成开关标识。
    [ScriptMethod(name:"金蝶跑圈横杆预测",eventType:EventTypeEnum.StartCasting,
        eventCondition:["ActionId:regex:^(34797|34798)$"],userControl:false)]
    public void RunBarCast(Event e,ScriptAccessory sa)=>ObserveBar(e,sa,true);
    private async void ObserveBar(Event e,ScriptAccessory sa,bool cast)
    {
        if(!Position(e["SourcePosition"],out var position)) return;
        if(position.X<165 || position.X>235 || position.Z< -160 || position.Z> -90 || MathF.Abs(position.Y-10)>2) return;
        CancellationToken token;
        BarState bar=bars[position.X<200?0:1];
        int generation;
        int delay=350, lifetime=4650;
        float rotation=(position.X<200?1:-1)*(e["ActionId"]=="34797"?-1:1)*MathF.PI/2;
        lock(gate)
        {
            if(!active || barPending==null) return;
            token=barPending.Token;
            var now=DateTime.Now;
            var at=e.DateTime==default?now:e.DateTime;
            if(cast)
            {
                if(bar.CastAt!=default && (at-bar.CastAt).TotalSeconds<0.5) return;
                bar.CastAt=at;
                // 既有下一轮预兆已覆盖读条时不重建；首轮或丢失结算时才补本次。
                if(bar.Pending || now<bar.VisibleUntil) return;
                Float(e["DurationMilliseconds"],out var duration);
                // EXD Action34797/34798 Cast100ms=15，历史实际到包约1.47至1.51秒。
                lifetime=Math.Clamp((int)Math.Max(duration,1500),1,5000);
                delay=0; rotation=-rotation;
            }
            else
            {
                if(bar.EffectAt!=default && (at-bar.EffectAt).TotalSeconds<0.5) return;
                bar.EffectAt=at;
            }
            generation=++bar.Generation; bar.Pending=true;
        }
        try
        {
            if(delay>0) await Task.Delay(delay,token);
            await sa.Method.RunOnMainThreadAsync(()=>
            {
                lock(gate)
                {
                    if(!active || token.IsCancellationRequested || generation!=bar.Generation) return;
                    bar.Pending=false;
                    var now=DateTime.Now;
                    // 仅新一轮交接时移除尚未到期的旧图；绝不逐帧删建。
                    if(bar.Handle!=0 && now<bar.VisibleUntil)
                        sa.Method.VfxMethod.RemoveVfx(bar.Handle,VfxType.Omen);
                    bar.Handle=sa.Method.VfxMethod.CreateOmen(532,new Vector3(8,0.01f,8),position,rotation,
                        new Vector4(1,0.12f,0.08f,0.80f),lifetime);
                    bar.VisibleUntil=bar.Handle==0?default:now.AddMilliseconds(lifetime);
                }
            });
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { sa.Log.Error("VF2 bar: "+ex.Message); }
        finally
        {
            lock(gate) { if(generation==bar.Generation) bar.Pending=false; }
        }
    }
    [ScriptMethod(name:"机关结算同步",eventType:EventTypeEnum.ActionEffect,eventCondition:["ActionId:regex:^(34774|34799|34800)$"],userControl:false)]
    public void Effect(Event e,ScriptAccessory sa)=>Observe(e,sa,false);
    [ScriptMethod(name:"提丰老师读条同步",eventType:EventTypeEnum.StartCasting,eventCondition:["ActionId:34800"],userControl:false)]
    public void Cast(Event e,ScriptAccessory sa)=>Observe(e,sa,true);
    private static bool Float(string value,out float result)=>float.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out result);
    private static bool Position(string value,out Vector3 position)
    {
        position=default;
        if(string.IsNullOrWhiteSpace(value)) return false;
        try { position=JsonConvert.DeserializeObject<Vector3>(value); }
        catch { return false; }
        return float.IsFinite(position.X) && float.IsFinite(position.Y) && float.IsFinite(position.Z);
    }
    private static int MatchTiming(uint action,Vector3 pos,Vector3 end,out bool reverse)
    {
        var from=new Vector2(pos.X,pos.Z); var to=new Vector2(end.X,end.Z);
        reverse=false;
        for(int i=0;i<Timings.Length;i++)
        {
            var t=Timings[i];
            if(t.Action!=action) continue;
            if(Vector2.DistanceSquared(from,t.A)<=0.75f*0.75f &&
                (action!=34774 || Vector2.DistanceSquared(to,t.B)<=0.75f*0.75f)) return i;
            if(action==34774 && Vector2.DistanceSquared(from,t.B)<=0.75f*0.75f &&
                Vector2.DistanceSquared(to,t.A)<=0.75f*0.75f) { reverse=true; return i; }
        }
        return -1;
    }
    private void Observe(Event e,ScriptAccessory sa,bool cast)
    {
        if(!uint.TryParse(e["ActionId"],out var id)) return;
        if(!Position(e["SourcePosition"],out var pos)) return;
        var end=pos;
        // 缺少终点时不能画零长度矩形或用上一次方向猜测。
        if(id==34774 && (!Position(e["EffectPosition"],out end) ||
            Vector2.DistanceSquared(new(pos.X,pos.Z),new(end.X,end.Z))<0.25f)) return;
        // Only the captured crystal-carrying arena; do not match round-three helpers.
        if(pos.X < -250 || pos.X > -150 || pos.Z<190 || pos.Z>310 || MathF.Abs(pos.Y-6)>3) return;
        bool rotationValid=Float(e["SourceRotation"],out var rotation) && float.IsFinite(rotation);
        if(id==34799 && !rotationValid) return;
        int profile=MatchTiming(id,pos,end,out bool reverse);
        string source=e["SourceId"];
        if(string.IsNullOrWhiteSpace(source) || source is "0" or "00000000")
        {
            if(profile<0 && id!=34800) return;
            source=id==34800?"Typhon":$"Profile{profile}";
        }
        var key=(id,source);
        lock(gate)
        {
            if(!active) return;
            if(!hazards.TryGetValue(key,out var h))
            {
                double forward=profile>=0?Timings[profile].Forward:0;
                double backward=profile>=0?Timings[profile].Reverse:0;
                h=new Hazard {Action=id,Position=pos,End=end,Rotation=rotation,
                    Anchor=profile>=0?new Vector3(Timings[profile].A.X,pos.Y,Timings[profile].A.Y):pos,
                    DrawName=$"{Prefix}{id}_{source}",
                    Cycle=id==34800?5.6057:0, ForwardCycle=forward,ReverseCycle=backward};
                hazards.Add(key,h);
            }
            var now=DateTime.Now;
            var eventAt=e.DateTime==default?now:e.DateTime;
            int phase=id==34774?(profile>=0?(reverse?1:0):
                (Vector2.DistanceSquared(new(pos.X,pos.Z),new(h.Anchor.X,h.Anchor.Z))<0.75f*0.75f?0:1)):0;
            if(cast)
            {
                h.Observed=now;
                Float(e["DurationMilliseconds"],out var duration);
                if(duration<=0) duration=3200;
                h.Next=now.AddMilliseconds(duration); breath=h;
            }
            else
            {
                if(h.EventAt!=default && (eventAt-h.EventAt).TotalSeconds<0.25) return;
                if(h.EventAt!=default)
                {
                    double interval=(eventAt-h.EventAt).TotalSeconds;
                    if(id==34800)
                    {
                        if(interval>=0.5 && interval<=12)
                        {
                            h.Cycle=h.BreathSamples==0?interval:h.Cycle*0.8+interval*0.2;
                            h.BreathSamples++;
                        }
                    }
                    else
                    {
                        double previous=h.Phase==0?h.ForwardCycle:h.ReverseCycle;
                        // 同向重复或漏过完整一轮时不把长间隔学成新周期。
                        bool alternating=id!=34774 || phase!=h.Phase;
                        if(alternating && interval>=0.5 && interval<=6 &&
                            (previous<=0 || interval>=previous*0.65 && interval<=previous*1.35))
                        {
                            double learned=previous<=0?interval:previous*0.8+interval*0.2;
                            if(h.Phase==0) h.ForwardCycle=learned; else h.ReverseCycle=learned;
                            if(h.ForwardCycle<=0) h.ForwardCycle=interval;
                            if(h.ReverseCycle<=0) h.ReverseCycle=interval;
                        }
                    }
                }
                h.Position=pos; h.End=end; h.Rotation=rotation; h.Phase=phase;
                if(id!=34800) h.Cycle=phase==0?h.ForwardCycle:h.ReverseCycle;
                h.EventAt=eventAt; h.Observed=now;
                h.Next=h.Cycle>0?now.AddSeconds(h.Cycle):default; h.Warned=false;
                if(id==34800) breath=h;
                else ScheduleHazard(h,sa);
            }
        }
    }
    private static void ScheduleHazard(Hazard h,ScriptAccessory sa)
    {
        if(h.Cycle<=0) return;
        var dp=sa.Data.GetDefaultDrawProperties(); dp.Name=h.DrawName;
        // 糖果串下一次从当前终点返回，交换两端但保留同一条实际走廊和原宽度。
        dp.Position=h.Action==34774?h.End:h.Position;
        dp.Rotation=h.Action==34774?0:h.Rotation; dp.FixRotation=true;
        var delta=h.End-h.Position;
        dp.Scale=h.Action==34799?new Vector2(4,4):new Vector2(3,MathF.Sqrt(delta.X*delta.X+delta.Z*delta.Z));
        if(h.Action==34774) dp.TargetPosition=h.Position;
        var color=sa.Data.DefaultDangerColor;
        dp.Color=new Vector4(color.X,color.Y,color.Z,0.80f);
        // 一次事件只安排一个预警窗口，交由 KDY 管理延迟和销毁，不逐帧重复创建。
        double preview=Math.Min(HazardPreviewSeconds,h.Cycle);
        dp.Delay=Math.Max(0,(int)((h.Cycle-preview)*1000));
        dp.DestoryAt=Math.Max(1,(int)(preview*1000));
        sa.Method.SendDraw(DrawModeEnum.Default,h.Action==34799?DrawTypeEnum.Straight:DrawTypeEnum.Rect,dp);
    }
    private void Update()
    {
        // 保留原版提丰提醒和数据清理。此回调不绘图，也不滚动预测下一周期。
        lock(gate)
        {
            if(!active || accessory==null || DateTime.Now<textAt) return;
            var sa=accessory; var now=DateTime.Now; textAt=now.AddMilliseconds(50);
            var player=sa.Data.MyObject;
            if(player==null || player.Position.X < -250 || player.Position.X > -150 || player.Position.Z<190 || player.Position.Z>310) return;
            if(EnableBreathReminder && breath!=null && !breath.Warned && breath.Next!=default && now-breath.Observed<TimeSpan.FromSeconds(12))
            {
                double seconds=(breath.Next-now).TotalSeconds;
                if(seconds>0 && seconds<=Math.Clamp(BreathWarningSeconds,0.1f,5))
                {
                    foreach(var status in player.StatusList)
                    {
                        if(status.StatusId!=3704 || status.Param!=37) continue;
                        breath.Warned=true;
                        sa.Method.TextInfo($"提丰老师即将吹气（约{seconds:F1}秒），注意安全区",Math.Clamp((int)(seconds*1000),500,5000),true);
                        break;
                    }
                }
            }
            if(now>=cleanupAt)
            {
                cleanupAt=now.AddSeconds(5);
                expired.Clear();
                foreach(var item in hazards)
                    if(now-item.Value.Observed>TimeSpan.FromSeconds(15)) expired.Add(item.Key);
                foreach(var key in expired)
                {
                    var h=hazards[key];
                    if(ReferenceEquals(breath,h)) breath=null;
                    hazards.Remove(key);
                }
            }
        }
    }
}
