using KodakkuAssist.Module.Draw;
using KodakkuAssist.Module.GameEvent;
using KodakkuAssist.Script;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System;
namespace VF3_AOE
{

[ScriptType(name: "糖豆人第三关画图", territorys: [1165], guid: "3a9a6d4c-5f3c-4d6d-9f0d-100000000001", version: "0.3.0.0", author: "MYY", note: "糖豆人第三关画图，基于VFALLGUYS的AOE绘图数据进行编写，纯AI编写。", updateInfo: "尝试不再依赖循环绘图。尝试优化代码。")]
public class VFALLGUYS_Third_AOE : IDisposable
{
    private const string Prefix = "VFALLGUYS_Third_AOE_";
    private readonly object gate = new();
    private ScriptAccessory? accessory;
    private GameEvents events = new();
    private Map3? map;
    private DateTime lastEvent;
    private DateTime nextWarning;
    private readonly Dictionary<string,DateTime> drawings = new(66);
    private readonly Dictionary<(uint,int),DateTime> effectsSeen=new(66);
    private readonly string[] drawNames=Enumerable.Range(0,66).Select(i=>Prefix+"AOE_"+i).ToArray();
    private readonly string[] drawPatterns=Enumerable.Range(0,66).Select(i=>"^"+Prefix+"AOE_"+i+"$").ToArray();
    private readonly DateTime[] firstLast=new DateTime[2];
    private readonly double[] firstCycle={2.56,2.56};
    private readonly int[] firstSequence=new int[2];
    [UserSetting("AOE 预览秒数（0.3—8）")] public float PreviewSeconds { get; set; } = 2.5f;
    [UserSetting("输出机制同步日志")] public bool DebugLogging { get; set; } = false;

    public void Init(ScriptAccessory sa)
    {
        Dispose();
        lock (gate)
        {
            accessory = sa; events = new(); lastEvent = default; Array.Clear(firstLast); Array.Clear(firstSequence); firstCycle[0]=firstCycle[1]=2.56;
            Service.Log = sa.Log;
            map = new Map3(events);
            // Predictions are emitted only from game events, never from a frame timer.
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            map?.Dispose(); map = null;
            accessory?.Method.RemoveDraw("^" + Prefix);
            accessory?.Method.ClearFrameworkUpdateAction(this);
            drawings.Clear(); effectsSeen.Clear();
        }
    }
    [ScriptMethod(name: "第三关机制同步", eventType: EventTypeEnum.ActionEffect, eventCondition: ["ActionId:regex:^(34801|34802|34804|34812|34796|29795)$"], userControl: false)]
    public void Effect(Event e, ScriptAccessory sa) => Observe(e, sa, false);
    [ScriptMethod(name: "方块读条同步", eventType: EventTypeEnum.StartCasting, eventCondition: ["ActionId:34812"], userControl: false)]
    public void Cast(Event e, ScriptAccessory sa) => Observe(e, sa, true);

    private void Observe(Event e, ScriptAccessory sa, bool cast)
    {
        // Original GameEvents reads caster position, NOT the packet's EffectPosition.
        if (!uint.TryParse(e["ActionId"], out var id)) return;
        Vector3 pos;
        try { if (string.IsNullOrWhiteSpace(e["SourcePosition"])) return; pos = JsonConvert.DeserializeObject<Vector3>(e["SourcePosition"]); }
        catch { return; }
        lock (gate)
        {
            if (accessory == null) return;
            if(lastEvent!=default && (e.DateTime<lastEvent.AddSeconds(-0.5) || e.DateTime>lastEvent.AddSeconds(20)))
            { map?.Dispose(); map=new Map3(events); sa.Method.RemoveDraw("^"+Prefix); drawings.Clear(); effectsSeen.Clear(); Array.Clear(firstLast); Array.Clear(firstSequence); firstCycle[0]=firstCycle[1]=2.56; }
            if (map == null) map = new Map3(events);
            int matched=-1;
            float nearest=2.25f;
            for(int i=0;i<map.AOEs.Count;i++)
            {
                var delta=pos-map.AOEs[i].Origin;
                float distance=delta.X*delta.X+delta.Z*delta.Z;
                if(distance<nearest && MathF.Abs(delta.Y)<2) {nearest=distance;matched=i;}
            }
            if(matched<0) return;
            if(!cast)
            {
                var key=(id,matched);
                if(effectsSeen.TryGetValue(key,out var previous) && Math.Abs((e.DateTime-previous).TotalSeconds)<0.2) return;
                effectsSeen[key]=e.DateTime;
                if(id==34802 && MathF.Abs(pos.Z-267.5f)<1.5f && MathF.Abs(pos.Y-6)<1.5f)
                {
                    int side=pos.X<0?-1:1;
                    int sideIndex=side<0?0:1;
                    if(firstLast[sideIndex]!=default)
                    {
                        double interval=(e.DateTime-firstLast[sideIndex]).TotalSeconds;
                        if(interval>1.8 && interval<3.5) firstCycle[sideIndex]=firstCycle[sideIndex]*0.5+interval*0.5;
                    }
                    firstLast[sideIndex]=e.DateTime;
                    double cycle=firstCycle[sideIndex];
                    // Creation and removal use framework scheduling. Unique names keep
                    // removal of an older prediction from matching a newer VFX.
                    // Each finite VFX expires without a per-cycle RemoveDraw.
                    string nextName=Prefix+"First_"+side+"_"+(++firstSequence[sideIndex]);
                    // Each side schedules one finite VFX after its own effect.
                    var dp=sa.Data.GetDefaultDrawProperties();
                    dp.Name=nextName;dp.Position=new Vector3(side*10,6,267.5f);
                    dp.Scale=new Vector2(5);dp.FixRotation=true;
                    dp.Wave=false;
                    double preview=Math.Clamp(PreviewSeconds,0.3f,1.5f);
                    dp.Delay=Math.Max(0,(int)((cycle-preview)*1000));
                    dp.DestoryAt=Math.Max(1,(int)(preview*1000));
                    dp.ScaleMode=ScaleMode.ByTime;
                    var color=sa.Data.DefaultDangerColor;
                    dp.Color=new Vector4(color.X,color.Y,color.Z,0.80f);
                    sa.Method.SendDraw(DrawModeEnum.Vfx,DrawTypeEnum.Circle,dp);
                    lastEvent=e.DateTime;
                    return;
                }
                for(int i=0;i<map.AOEs.Count;i++)
                {
                    var a=map.AOEs[i];
                    if(Vector3.DistanceSquared(pos,a.Origin)>=2.25f) continue;
                    string name=drawNames[i];
                    if(drawings.Remove(name)) sa.Method.RemoveDraw(drawPatterns[i]);
                }
            }
            map.RouteDiagnostics = DebugLogging;
            if (cast) events.Cast(id,pos); else events.Effect(id,pos);
            lastEvent = e.DateTime;
            Tick();
        }
    }
    private void Tick()
    {
        lock (gate)
        {
            var sa = accessory; var m = map; var now = DateTime.Now;
            if (sa == null || m == null) return;
            try
            {
                var player = sa.Data.MyObject;
                if (player == null || player.CurrentHp == 0) return;
                var pos = player.Position;
                if(pos.Z<123 || pos.Z>330 || MathF.Abs(pos.X)>25) return;
                float configuredPreview=Math.Clamp(PreviewSeconds,0.3f,8);
                for(int i=0;i<m.AOEs.Count;i++)
                {
                    var a=m.AOEs[i];
                    if(MathF.Abs(a.Origin.Z-267.5f)<0.1f && MathF.Abs(a.Origin.Y-6)<0.1f) continue;
                    if(a.NextActivation==default) continue;
                    float remaining=a.TimeUntilNextActivation(now);
                    float preview=configuredPreview;
                    if(MathF.Abs(a.Origin.Z-225.3f)<0.1f && MathF.Abs(a.Origin.Y-14.76f)<0.1f)
                        preview=Math.Min(preview,1.5f);
                    if(remaining<=0 || remaining>preview) continue;
                    // Finite, event-triggered windows. No permanent nodes or render callbacks.
                    string name=drawNames[i];
                    if(drawings.TryGetValue(name,out var expires) && now<expires) continue;
                    var dp=sa.Data.GetDefaultDrawProperties(); dp.Name=name;
                    // Straight is centred; Rect starts at its origin and would shift the footprint.
                    dp.Position=a.Origin; dp.Scale=a.Type==Map.AOEShape.Square?new Vector2(a.R1*2):new Vector2(a.R1);
                    dp.Rotation=0; dp.FixRotation=true; dp.Wave=false; dp.Color=sa.Data.DefaultDangerColor;
                    dp.Color=new Vector4(dp.Color.X,dp.Color.Y,dp.Color.Z,0.80f);
                    dp.Delay=Math.Max(0,(int)((remaining-preview)*1000));
                    dp.DestoryAt=Math.Max(1,(int)(MathF.Min(remaining,preview)*1000));
                    dp.ScaleMode=ScaleMode.ByTime;
                    sa.Method.SendDraw(DrawModeEnum.Vfx,a.Type==Map.AOEShape.Square?DrawTypeEnum.Straight:DrawTypeEnum.Circle,dp);
                    drawings[name]=now.AddMilliseconds(dp.Delay+dp.DestoryAt);
                }

            }
            catch(Exception ex)
            {

                if(now>=nextWarning) { nextWarning=now.AddSeconds(5); sa.Log.Error("VF3: "+ex.Message); }
            }
        }
    }

}
internal static class Service { internal static ScriptAccessory.LogAccessory? Log; }
public sealed class GameEvents
{
    public event Action<uint,Vector3>? ActionEffectEvent;
    public event Action<uint,Vector3>? StartCastEvent;
    public void Effect(uint id,Vector3 p)=>ActionEffectEvent?.Invoke(id,p);
    public void Cast(uint id,Vector3 p)=>StartCastEvent?.Invoke(id,p);
}
public sealed class DeveloperDiagnostics
{
    public bool Enabled;
    public void Info(string message) { if(Enabled) Service.Log?.Debug(message); }
}

public class Map : IDisposable {
public enum AOEShape
	{
		None,
		Circle,
		Square,
		Rect
	}public class RepeatingAOE
	{
		public AOEShape Type;

		public float R1;

		public float R2;

		public float Rotation;

		public Vector3 Origin;

		public float Repeat;

		public float SeqDelay;

		public DateTime NextActivation;

		public float TimeUntilNextActivation(DateTime now)
		{
			if (NextActivation == default(DateTime))
			{
				return float.MaxValue;
			}
			float num = (float)(NextActivation - now).TotalSeconds;
			if (!(num >= 0f))
			{
				return Repeat > 0 ? (num % Repeat + Repeat) % Repeat : float.MaxValue;
			}
			return num;
		}

		public bool Contains(Vector3 p, float margin = 0)
		{
			float radius = R1 + margin;
			float width = R2 + margin;
			float bound = Type == AOEShape.Rect ? MathF.Sqrt(radius * radius + width * width) : radius;
			if (MathF.Abs(p.X - Origin.X) > bound || MathF.Abs(p.Z - Origin.Z) > bound) return false;
			return Type switch
			{
				AOEShape.Circle => ((p.X-Origin.X)*(p.X-Origin.X)+(p.Z-Origin.Z)*(p.Z-Origin.Z)) <= radius * radius,
				AOEShape.Square => MathF.Max(MathF.Abs(p.X-Origin.X),MathF.Abs(p.Z-Origin.Z)) <= radius,
				AOEShape.Rect => false,
				_ => false,
			};
		}



	}public class AOESequence
	{
		public int StartIndex;

		public int Count;

		public int NextIndex;

		public int FirstIndex;
		public int LastObservedIndex = -1;
		public DateTime LastObservedAt;
		public DateTime NextDiagnostic;
		public int PhaseAnomalies;
		public int[] IntervalSamples = [];
		public float[] PendingIntervals = [];
	}
public List<RepeatingAOE> AOEs = new(); public readonly DeveloperDiagnostics Diagnostics = new(); public bool RouteDiagnostics { get => Diagnostics.Enabled; set => Diagnostics.Enabled=value; } public virtual void Dispose() {}
protected AOESequence CreateSequence(AOEShape type, float r1, float r2, IEnumerable<(Vector3 pos, float rot, float delay)> instances)
	{
		int count = AOEs.Count;
		float num = 0f;
		foreach (var (origin, rotation, num2) in instances)
		{
			AOEs.Add(new RepeatingAOE
			{
				Type = type,
				R1 = r1,
				R2 = r2,
				Rotation = rotation,
				Origin = origin,
				SeqDelay = num2
			});
			num += num2;
		}
		foreach (RepeatingAOE item in AOEs.Skip(count))
		{
			item.Repeat = num;
		}
		return new AOESequence
		{
			StartIndex = count,
			Count = AOEs.Count - count,
			FirstIndex = -1
		};
	}
protected void UpdateSequence(Vector3 pos, float activateIn, params AOESequence[] candidates)
	{
		UpdateSequence(pos, activateIn, candidates.AsEnumerable());
	}
protected void UpdateSequence(Vector3 pos, float activateIn, IEnumerable<AOESequence> candidates)
	{
		var matches = candidates.Select(c => (sequence: c, index: FindIndexInSequence(c, pos)))
			.Where(m => m.index >= 0)
			.OrderBy(m => Vector3.DistanceSquared(AOEs[m.sequence.StartIndex + m.index].Origin, pos));
		foreach (var match in matches)
		{
			AOESequence candidate = match.sequence;
			int num = match.index;
			if (num < 0)
			{
				continue;
			}
			if (AOEs[candidate.StartIndex].NextActivation == default(DateTime))
			{
				Diagnostics.Info($"Starting sequence @ {candidate.StartIndex} from {num}");
				candidate.FirstIndex = num;
			}
			DateTime observed = DateTime.Now;
			var current = AOEs[candidate.StartIndex + num];
			if(candidate.StartIndex==46 && RouteDiagnostics) {
				float gap=candidate.LastObservedIndex<0?float.NaN:(float)(observed-candidate.LastObservedAt).TotalSeconds;
				Diagnostics.Info($"AOE interval: seq=46 node={num} previous={candidate.LastObservedIndex} observedAt={observed:O} gap={gap:f4} cast={activateIn:f2} delays={string.Join(",",AOEs.Skip(46).Take(candidate.Count).Select(a=>a.SeqDelay.ToString("f4",System.Globalization.CultureInfo.InvariantCulture)))}");
			}
			double phaseError = current.NextActivation == default ? 0 :
				(observed.AddSeconds(activateIn) - current.NextActivation).TotalSeconds;
			if (current.Repeat > 0) phaseError -= Math.Round(phaseError / current.Repeat) * current.Repeat;
			Diagnostics.Info($"AOE event seq={candidate.StartIndex} node={num} at={observed:O} cast={activateIn:f2} error={phaseError:f3}");
			// A single out-of-order effect must not shift every mechanism in a sequence.
			// Two consecutive large discrepancies permit recovery after a real reset.
			if (Math.Abs(phaseError) > 0.8 && current.NextActivation != default)
			{
				if (++candidate.PhaseAnomalies < 2) return;
			}
			candidate.PhaseAnomalies = 0;
			if (observed >= candidate.NextDiagnostic)
			{
				double error = current.NextActivation == default ? double.NaN :
					(observed.AddSeconds(activateIn) - current.NextActivation).TotalSeconds;
				if (current.Repeat > 0 && double.IsFinite(error)) error -= Math.Round(error / current.Repeat) * current.Repeat;
				Diagnostics.Info($"AOE sync seq={candidate.StartIndex} node={num} error={error:f3}s interval={current.SeqDelay:f3}s cycle={current.Repeat:f3}s pos={pos} origin={current.Origin} cast={activateIn:f2}");
				candidate.NextDiagnostic = observed.AddSeconds(3);
			}
			if (activateIn == 0)
			{
				if (candidate.IntervalSamples.Length != candidate.Count)
				{
					candidate.IntervalSamples = new int[candidate.Count];
					candidate.PendingIntervals = new float[candidate.Count];
				}
				if (candidate.LastObservedIndex >= 0 && num == (candidate.LastObservedIndex + 1) % candidate.Count)
				{
					var previous = AOEs[candidate.StartIndex + candidate.LastObservedIndex];
					float interval = (float)(observed - candidate.LastObservedAt).TotalSeconds;
					int index = candidate.LastObservedIndex;
					if (interval >= 0.25f && interval <= 2.1f &&
						(Math.Abs(interval - previous.SeqDelay) <= 0.25f ||
						(candidate.Count == 4 && (Math.Abs(interval - 1.4f) < 0.12f || Math.Abs(interval - 1.9f) < 0.12f))))
					{
						if (candidate.IntervalSamples[index] == 0 || Math.Abs(candidate.PendingIntervals[index] - interval) <= 0.08f)
						{
							candidate.PendingIntervals[index] = candidate.IntervalSamples[index] == 0 ? interval :
								(candidate.PendingIntervals[index] * candidate.IntervalSamples[index] + interval) / (candidate.IntervalSamples[index] + 1);
							candidate.IntervalSamples[index]++;
							if (candidate.IntervalSamples[index] >= 3)
							{
								float adjusted = Math.Abs(previous.SeqDelay - candidate.PendingIntervals[index]) > 0.25f
									? candidate.PendingIntervals[index] : previous.SeqDelay * 0.8f + candidate.PendingIntervals[index] * 0.2f;
								// Four-point exaflares contain one long gap. Replacing one
								// interval alone can temporarily erase it or create two gaps.
								if(candidate.Count==4 && Math.Abs(adjusted-previous.SeqDelay)>0.25f) {
									bool wholeCycle=candidate.PendingIntervals.All(v=>v>0) &&
										Math.Abs(candidate.PendingIntervals.Sum()-6.1f)<0.2f;
									if(wholeCycle) for(int k=0;k<candidate.Count;k++) AOEs[candidate.StartIndex+k].SeqDelay=candidate.PendingIntervals[k];
								} else previous.SeqDelay=adjusted;
								float cycle = AOEs.Skip(candidate.StartIndex).Take(candidate.Count).Sum(a => a.SeqDelay);
								foreach (var a in AOEs.Skip(candidate.StartIndex).Take(candidate.Count)) a.Repeat = cycle;
								candidate.IntervalSamples[index] = 0;
							}
						}
						else { candidate.IntervalSamples[index] = 1; candidate.PendingIntervals[index] = interval; }
					}
					else candidate.IntervalSamples[index] = 0;
				}
				candidate.LastObservedIndex = num;
				candidate.LastObservedAt = observed;
			}
			if (activateIn > 0f)
			{
				candidate.NextIndex = num;
				DateTime nextActivation = observed.AddSeconds(activateIn);
				for (int i = 0; i < candidate.Count; i++)
				{
					RepeatingAOE repeatingAOE = AOEs[candidate.StartIndex + (candidate.NextIndex + i) % candidate.Count];
					repeatingAOE.NextActivation = nextActivation;
					nextActivation = nextActivation.AddSeconds(repeatingAOE.SeqDelay);
				}
			}
			else
			{
				candidate.NextIndex = (num + 1) % candidate.Count;
				DateTime dateTime = observed.AddSeconds(current.SeqDelay);
				for (int j = 0; j < candidate.Count; j++)
				{
					RepeatingAOE repeatingAOE2 = AOEs[candidate.StartIndex + (candidate.NextIndex + j) % candidate.Count];
					repeatingAOE2.NextActivation = dateTime;
					dateTime = dateTime.AddSeconds(repeatingAOE2.SeqDelay);
				}
			}
			return;
		}
		Diagnostics.Info($"Failed to find AOE at {pos}");
	}
protected int FindIndexInSequence(AOESequence sequence, Vector3 pos)
	{
		int best = -1;
		float distance = float.MaxValue;
		for (int i = 0; i < sequence.Count; i++)
		{
			if (AOEs[sequence.StartIndex + i].Contains(pos))
			{
				float d = Vector3.DistanceSquared(AOEs[sequence.StartIndex + i].Origin, pos);
				if (d < distance) { distance = d; best = i; }
			}
		}
		return best;
	}
}
public class Map3 : Map {
private readonly GameEvents events;
private AOESequence _mech1Rotating;
private AOESequence _mech2Exaflares;
private AOESequence _mech3Rotating;
private AOESequence _mech3Exaflares;
private AOESequence _mech4RectsL;
private AOESequence _mech4RectsR;
private AOESequence _mech4RectsC;
private AOESequence _mech4Exaflare;
private AOESequence _mech5PairL1;
private AOESequence _mech5PairL2;
private AOESequence _mech5PairR1;
private AOESequence _mech5PairR2;
private AOESequence _mech6Exaflare;
private AOESequence _mech7PairL;
private AOESequence _mech7PairR;
public Map3(GameEvents events)
{
this.events=events; events.ActionEffectEvent += OnActionEffect; events.StartCastEvent += OnStartCast;
		_mech1Rotating = CreateRotatingSequence(1.2f, 6f, 267.5f, -10f, 10f);
		_mech2Exaflares = CreateDoubleExaflareSequence(9.39f, 251f, 11.75f, 243f);
		_mech3Rotating = CreateRotatingSequence(1f, 14.76f, 225.3f, -10f, 0f, 10f);
		_mech3Exaflares = CreateDoubleExaflareSequence(14.54f, 229.3f, 14.97f, 221.3f);
		_mech4RectsL = CreateRectsSequence(-12f, -6f);
		_mech4RectsR = CreateRectsSequence(12f, 6f);
		_mech4RectsC = CreateRectsSequenceCenter();
		_mech4Exaflare = CreateSingleExaflareSequence(22.52f, 198.7f, -12f, -4f, 4f, 12f);
		_mech5PairL1 = CreatePairsSequence(5f, new Vector3(-10f, 29.95f, 170f), new Vector3(-2f, 29.95f, 170f));
		_mech5PairL2 = CreatePairsSequence(5f, new Vector3(-10f, 31.39f, 156f), new Vector3(-4.34f, 30.81f, 161.66f));
		_mech5PairR1 = CreatePairsSequence(5f, new Vector3(10f, 29.95f, 170f), new Vector3(4.34f, 29.37f, 175.66f));
		_mech5PairR2 = CreatePairsSequence(5f, new Vector3(10f, 31.39f, 156f), new Vector3(2f, 31.39f, 156f));
		// Observed events place the long interval after +4, not after the last array entry.
		_mech6Exaflare = CreateSequence(AOEShape.Circle, 6, 0,
			new float[] { -4, 4, 12, -12 }.Select((x, i) => (new Vector3(x, 33.47f, 145.7f), 0f, i == 1 ? 1.9f : 1.4f)));
		_mech7PairL = CreatePairsSequence(3f, new Vector3(-6.78f, 35.91f, 136.91f), new Vector3(-3.22f, 36.3f, 135.09f));
		_mech7PairR = CreatePairsSequence(3f, new Vector3(6.78f, 35.91f, 136.91f), new Vector3(3.22f, 36.3f, 135.09f));

	}
private void OnActionEffect(uint actionId, Vector3 casterPos)
	{

		switch (actionId)
		{
		case 34801u:
			UpdateSequence(casterPos, 0f, _mech2Exaflares, _mech3Exaflares, _mech4Exaflare, _mech6Exaflare);
			break;
		case 34802u:
			UpdateSequence(casterPos, 0f, _mech1Rotating, _mech3Rotating);
			break;
		case 34804u:
		case 34812u:
			UpdateSequence(casterPos, 0f, _mech4RectsL, _mech4RectsR, _mech4RectsC);
			break;
		case 34796u:
			UpdateSequence(casterPos, 0f, _mech5PairL1, _mech5PairL2, _mech5PairR1, _mech5PairR2);
			break;
		case 29795u:
			UpdateSequence(casterPos, 0f, _mech7PairL, _mech7PairR);
			break;
		}
	}
private void OnStartCast(uint actionId, Vector3 casterPos)
	{
		if (actionId == 34812)
		{

			UpdateSequence(casterPos, 1f, _mech4RectsL, _mech4RectsR);
		}
	}
private AOESequence CreateRotatingSequence(float repeat, float y, float z, params float[] x)
	{
		return CreateSequence(AOEShape.Circle, 5f, 0f, x.Select((float x2) => (new Vector3(x2, y, z), 0f, repeat: repeat)));
	}
private AOESequence CreateSingleExaflareSequence(float y, float z, params float[] x)
	{
		return CreateSequence(AOEShape.Circle, 6f, 0f, x.Select((float xx) => (new Vector3(xx, y, z), 0f, (xx == x.Last()) ? 1.9f : 1.4f)));
	}
private AOESequence CreateDoubleExaflareSequence(float y1, float z1, float y2, float z2)
	{
		float[] source = new float[4] { -12f, -4f, 4f, 12f };
		IEnumerable<(Vector3, float, float)> first = source.Select((float x) => (new Vector3(x, y1, z1), 0f, 1.4f));
		IEnumerable<(Vector3, float, float)> second = source.Select((float x) => (new Vector3(x, y2, z2), 0f, 1.4f));
		return CreateSequence(AOEShape.Circle, 6f, 0f, first.Concat(second));
	}
private AOESequence CreateRectsSequence(float x1, float x2)
	{
		(float, float, float)[] source = new(float, float, float)[5]
		{
			(25.59f, 190.4f, 0.5f),
			(23.37f, 196.4f, 0.5f),
			(21.15f, 202.4f, 0.5f),
			(18.94f, 208.4f, 0.5f),
			(16.73f, 214.4f, 1.1f)
		};
		IEnumerable<(Vector3, float, float)> first = source.Select(((float y, float z, float d) e) => (new Vector3(x1, e.y, e.z), 0f, d: e.d));
		IEnumerable<(Vector3, float, float)> second = source.Select(((float y, float z, float d) e) => (new Vector3(x2, e.y, e.z), 0f, d: e.d));
		return CreateSequence(AOEShape.Square, 3f, 0f, first.Concat(second));
	}
private AOESequence CreateRectsSequenceCenter()
	{
		IEnumerable<(Vector3, float, float)> instances = new(float, float, float)[5]
		{
			(25.59f, 190.4f, 0.5f),
			(23.37f, 196.4f, 0.5f),
			(21.15f, 202.4f, 0.5f),
			(18.94f, 208.4f, 0.5f),
			(16.73f, 214.4f, 4.2f)
		}.Select(((float y, float z, float d) e) => (new Vector3(0f, e.y, e.z), 0f, d: e.d));
		return CreateSequence(AOEShape.Square, 3f, 0f, instances);
	}
private AOESequence CreatePairsSequence(float r, Vector3 p1, Vector3 p2)
	{
		return CreateSequence(AOEShape.Circle, r, 0f, new(Vector3, float, float)[2]
		{
			(p1, 0f, 2.5f),
			(p2, 0f, 2.5f)
		});
	}
public override void Dispose() { events.ActionEffectEvent -= OnActionEffect; events.StartCastEvent -= OnStartCast; }
}

}
