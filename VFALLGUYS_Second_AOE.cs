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

[ScriptType(name: "糖豆人第二关画图", territorys: [1165], guid: "3a9a6d4c-5f3c-4d6d-9f0d-100000000011", version: "0.4.0.0", author: "MYY", note: "糖豆人第二关画图，包含搬水晶相关危险区预测和提丰老师吹飞预警，金蝶跑圈仅做了横杆预测。纯AI编写，反馈请到GITHUB或者你能找到我并私聊我。")]
public sealed class VFALLGUYS_Second_AOE : IDisposable
{
    private const string Prefix="VF2AOE_";
    // 34774: 糖果串机关；34799: 拳头机关；34800: 提丰老师；34716: 斧头机关。
    private readonly object gate=new();
    private readonly Dictionary<(uint Action,int X,int Z),Hazard> hazards=new(32);
    private readonly List<(uint Action,int X,int Z)> expired=new(32);
    private ScriptAccessory? accessory;
    private string? frame;
    private DateTime textAt;
    private DateTime drawAt;
    private DateTime cleanupAt;
    private Hazard? breath;
    private bool active;
    private CancellationTokenSource barPending=new();
    private const float HazardPreviewSeconds = 1.5f;
    private sealed class Hazard
    {
        public uint Action;
        public string DrawName="";
        public Vector3 Position, End;
        public float Rotation;
        public DateTime Last, Next, CastAt;
        public DateTime Observed;
        public double Cycle;
        public int Samples;
        public bool Warned;
        public DateTime DrawingUntil;
    }
    [UserSetting("提丰老师吹气提前提醒开关")] public bool EnableBreathReminder {get;set;}=true;
    [UserSetting("吹气前X秒提醒（0.1至5秒）")] public float BreathWarningSeconds {get;set;}=2;
    public void Init(ScriptAccessory sa)
    {
        Dispose();
        lock(gate) { barPending=new(); accessory=sa; active=true; textAt=default; drawAt=default; cleanupAt=default; frame=sa.Method.RegistFrameworkUpdateAction(Update,true); }
    }
    public void Dispose()
    {
        lock(gate)
        {
            active=false;
            barPending.Cancel();
            if(frame!=null) accessory?.Method.UnregistFrameworkUpdateAction(frame);
            accessory?.Method.ClearFrameworkUpdateAction(this);
            accessory?.Method.RemoveDraw("^"+Prefix);
            frame=null; hazards.Clear(); expired.Clear(); breath=null;
        }
    }
    [ScriptMethod(name:"金蝶跑圈横杆预测",eventType:EventTypeEnum.ActionEffect,
        eventCondition:["ActionId:regex:^(34797|34798)$"],userControl:true)]
    public async void PredictRunBar(Event e,ScriptAccessory sa)
    {
        Vector3 position;
        try { position=JsonConvert.DeserializeObject<Vector3>(e["SourcePosition"]); }
        catch { return; }
        if(position.X<165 || position.X>235 || position.Z< -160 || position.Z> -90 || MathF.Abs(position.Y-10)>2) return;
        CancellationToken token;
        lock(gate) { if(!active) return; token=barPending.Token; }
        float rotation=(position.X<200?1:-1)*(e["ActionId"]=="34797"?-1:1)*MathF.PI/2;
        try
        {
            // Join the next alternating preview after the current omen expires.
            await Task.Delay(350,token);
            await sa.Method.RunOnMainThreadAsync(()=>
            {
                if(!token.IsCancellationRequested)
                    sa.Method.VfxMethod.CreateOmen(532,new Vector3(8,0.01f,8),position,rotation,
                        new Vector4(1,0.12f,0.08f,0.44f),4650);
            });
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { sa.Log.Error("VF2 bar: "+ex.Message); }
    }
    [ScriptMethod(name:"机关结算同步",eventType:EventTypeEnum.ActionEffect,eventCondition:["ActionId:regex:^(34774|34799|34800)$"],userControl:false)]
    public void Effect(Event e,ScriptAccessory sa)=>Observe(e,sa,false);
    [ScriptMethod(name:"提丰老师读条同步",eventType:EventTypeEnum.StartCasting,eventCondition:["ActionId:34800"],userControl:false)]
    public void Cast(Event e,ScriptAccessory sa)=>Observe(e,sa,true);
    private static bool Float(string value,out float result)=>float.TryParse(value,NumberStyles.Float,CultureInfo.InvariantCulture,out result);
    private void Observe(Event e,ScriptAccessory sa,bool cast)
    {
        if(!uint.TryParse(e["ActionId"],out var id)) return;
        Vector3 pos,end;
        try { pos=JsonConvert.DeserializeObject<Vector3>(e["SourcePosition"]); end=id==34774 && !string.IsNullOrWhiteSpace(e["EffectPosition"])?JsonConvert.DeserializeObject<Vector3>(e["EffectPosition"]):pos; }
        catch {return;}
        // Only the captured crystal-carrying arena; do not match round-three helpers.
        if(pos.X < -250 || pos.X > -150 || pos.Z<190 || pos.Z>310 || MathF.Abs(pos.Y-6)>3) return;
        Float(e["SourceRotation"],out var rotation);
        var key=(id,(int)MathF.Round(pos.X*10),(int)MathF.Round(pos.Z*10));
        lock(gate)
        {
            if(!active) return;
            if(!hazards.TryGetValue(key,out var h))
            {
                h=new Hazard {Action=id,Position=pos,End=end,Rotation=rotation,
                    DrawName=$"{Prefix}{key.Item1}_{key.Item2}_{key.Item3}",
                    Cycle=id switch {34800=>5.6057,34774=>4.009,34799=>4.098,_=>0}};
                hazards.Add(key,h);
            }
            h.Position=pos; h.End=end; h.Rotation=rotation;
            var now=DateTime.Now;
            h.Observed=now;
            if(cast)
            {
                Float(e["DurationMilliseconds"],out var duration);
                if(duration<=0) duration=3200;
                h.CastAt=now.AddMilliseconds(duration); h.Next=h.CastAt; breath=h;
            }
            else
            {
                if(h.Last!=default && (now-h.Last).TotalSeconds<0.25) return;
                if(h.Last!=default)
                {
                    double interval=(now-h.Last).TotalSeconds;
                    if(interval>=0.5 && interval<=12) { h.Cycle=h.Samples==0?interval:h.Cycle*0.8+interval*0.2; h.Samples++; }
                }
                h.Last=now; h.CastAt=default; h.Next=h.Cycle>0?now.AddSeconds(h.Cycle):default; h.Warned=false;
                if(id==34800) breath=h;
            }
        }
    }
    private void Update()
    {
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
            if(now<drawAt) return;
            drawAt=now.AddMilliseconds(100);
            foreach(var item in hazards)
            {
                var h=item.Value;
                if(h.Action is 34800 or 34716) continue;
                // First observation anchors the measured default period; later observations refine it.
                if(h.Last==default || h.Next==default || h.Cycle<=0) continue;
                if(h.Last!=default && now-h.Last>TimeSpan.FromSeconds(15)) continue;
                double remaining=h.Next==default?double.PositiveInfinity:(h.Next-now).TotalSeconds;
                if(h.CastAt==default && h.Cycle>0 && remaining<0) remaining=(remaining%h.Cycle+h.Cycle)%h.Cycle;
                if(remaining<=0 || remaining>HazardPreviewSeconds) continue;
                // One VFX per preview window; repeated creation restarts its appearance animation.
                if(now<h.DrawingUntil) continue;
                var dp=sa.Data.GetDefaultDrawProperties(); dp.Name=h.DrawName;
                // TargetPosition already resolves the bearing; Rotation is an additive offset.
                dp.Position=h.Position; dp.Rotation=h.Action==34774?0:h.Rotation; dp.FixRotation=true;
                var delta=h.End-h.Position;
                dp.Scale=h.Action==34799?new Vector2(4,4):new Vector2(3,MathF.Sqrt(delta.X*delta.X+delta.Z*delta.Z));
                if(h.Action==34774) dp.TargetPosition=h.End;
                var color=sa.Data.DefaultDangerColor;
                dp.Color=new Vector4(color.X,color.Y,color.Z,0.44f);
                dp.DestoryAt=Math.Max(1,(int)(remaining*1000));
                sa.Method.SendDraw(DrawModeEnum.Default,h.Action==34799?DrawTypeEnum.Straight:DrawTypeEnum.Rect,dp);
                h.DrawingUntil=now.AddSeconds(remaining);
            }
        }
    }
}
