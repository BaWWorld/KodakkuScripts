using KodakkuAssist.Module.Draw;
using KodakkuAssist.Module.GameEvent;
using KodakkuAssist.Script;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System;
namespace VF3_AOE
{

[ScriptType(name: "糖豆人第三关画图", territorys: [1165], guid: "3a9a6d4c-5f3c-4d6d-9f0d-100000000001", version: "0.3.0.3", author: "MYY", note: "糖豆人第三关画图，基于VFALLGUYS的AOE绘图数据进行编写，纯AI编写。", updateInfo: "不知道写什么了 反正按照AI说的写了一些东西。")]
public class VFALLGUYS_Third_AOE : IDisposable
{
    private const string Prefix = "VFALLGUYS_Third_AOE_";
    private readonly object gate = new();
    private ScriptAccessory? accessory;
    private GameEvents events = new();
    private Map3? map;
    private DateTime lastEvent;
    private DateTime initializedAt;
    private DateTime nextWarning;
    private string drawPrefix = "";
    private long drawSequence;
    private readonly Prediction[] drawings = new Prediction[66];
    private readonly Dictionary<(uint,int),DateTime> effectsSeen=new(66);
    private readonly DateTime[] firstLast=new DateTime[2];
    private readonly double[] firstCycle={2.56,2.56};
    public float PreviewSeconds { get; set; } = 2.5f;
    public bool DebugLogging { get; set; } = false;

    private struct Prediction
    {
        public string? Name;
        public DateTime Activation;
        public DateTime VisibleAt;
    }

    public void Init(ScriptAccessory sa)
    {
        lock (gate)
        {
            Dispose();
            accessory = sa;
            initializedAt = DateTime.Now;
            Service.Log = sa.Log;
            ResetSynchronization(sa);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            var sa = accessory;
            accessory = null;
            var oldMap = map;
            map = null;
            string oldPrefix = drawPrefix;
            drawPrefix = "";
            Array.Clear(drawings);
            effectsSeen.Clear();
            try { oldMap?.Dispose(); }
            catch (Exception ex) { if (sa != null) ReportError(sa, ex); }
            if (sa != null)
            {
                if (oldPrefix.Length > 0) _ = CleanupDrawsAsync(sa, "^" + oldPrefix);
                try { sa.Method.ClearFrameworkUpdateAction(this); }
                catch (Exception ex) { ReportError(sa, ex); }
                if (ReferenceEquals(Service.Log, sa.Log)) Service.Log = null;
            }
        }
    }
    [ScriptMethod(name: "第三关机制同步", eventType: EventTypeEnum.ActionEffect, eventCondition: ["ActionId:regex:^(34801|34802|34804|34812|34796|29795)$"], userControl: false)]
    public void Effect(Event e, ScriptAccessory sa) => Observe(e, sa, false);
    [ScriptMethod(name: "方块读条同步", eventType: EventTypeEnum.StartCasting, eventCondition: ["ActionId:34812"], userControl: false)]
    public void Cast(Event e, ScriptAccessory sa) => Observe(e, sa, true);

    private void Observe(Event e, ScriptAccessory sa, bool cast)
    {
        try { ObserveCore(e, sa, cast); }
        catch (Exception ex) { ReportError(sa, ex); }
    }

    private void ObserveCore(Event e, ScriptAccessory sa, bool cast)
    {
        // Original GameEvents reads caster position, NOT the packet's EffectPosition.
        if (!uint.TryParse(e["ActionId"], out var id)) return;
        Vector3 pos;
        try { if (string.IsNullOrWhiteSpace(e["SourcePosition"])) return; pos = JsonConvert.DeserializeObject<Vector3>(e["SourcePosition"]); }
        catch { return; }
        lock (gate)
        {
            if (accessory == null || map == null || e.DateTime < initializedAt) return;
            int matched=-1;
            float nearest=2.25f;
            for(int i=0;i<map.AOEs.Count;i++)
            {
                var delta=pos-map.AOEs[i].Origin;
                float distance=delta.X*delta.X+delta.Z*delta.Z;
                if(distance<nearest && MathF.Abs(delta.Y)<2) {nearest=distance;matched=i;}
            }
            if(matched<0 || !Map3.MatchesAction(id, matched, cast)) return;
            // Unrelated events cannot reset the map. Late thread-pool work is dropped
            // rather than interpreted as a replay seek and clearing every warning.
            if(lastEvent!=default && e.DateTime<lastEvent.AddSeconds(-0.5)) return;
            if(lastEvent!=default && e.DateTime>lastEvent.AddSeconds(20))
                ResetSynchronization(sa);
            var now = DateTime.Now;
            if(!cast)
            {
                var key=(id,matched);
                if(effectsSeen.TryGetValue(key,out var previous) && e.DateTime<previous.AddSeconds(0.2)) return;
                effectsSeen[key]=e.DateTime;
                RetirePrediction(sa, matched);
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
                    // Only this side's own effect starts its next finite prediction.
                    SchedulePrediction(sa, matched, e.DateTime.AddSeconds(cycle),
                        cycle, Math.Min(ConfiguredPreview(), 1.5f), now);
                    if (e.DateTime > lastEvent) lastEvent=e.DateTime;
                    SchedulePredictions(sa, now);
                    return;
                }
            }
            map!.RouteDiagnostics = DebugLogging;
            if (cast) events.Cast(id,pos,e.DateTime); else events.Effect(id,pos,e.DateTime);
            if (e.DateTime > lastEvent) lastEvent = e.DateTime;
            SchedulePredictions(sa, now);
        }
    }

    private void ResetSynchronization(ScriptAccessory sa)
    {
        string oldPrefix = drawPrefix;
        map?.Dispose();
        events = new();
        map = new Map3(events);
        drawPrefix = Prefix + Guid.NewGuid().ToString("N") + "_";
        drawSequence = 0;
        Array.Clear(drawings);
        effectsSeen.Clear();
        Array.Clear(firstLast);
        firstCycle[0] = firstCycle[1] = 2.56;
        lastEvent = default;
        if (oldPrefix.Length > 0) _ = CleanupDrawsAsync(sa, "^" + oldPrefix);
    }

    private float ConfiguredPreview() =>
        float.IsFinite(PreviewSeconds) ? Math.Clamp(PreviewSeconds, 0.3f, 8f) : 2.5f;

    private void SchedulePredictions(ScriptAccessory sa, DateTime now)
    {
        var m = map;
        var player = sa.Data.MyObject;
        if (m == null || player == null || player.CurrentHp == 0) return;
        var pos = player.Position;
        if(pos.Z<123 || pos.Z>330 || MathF.Abs(pos.X)>25) return;
        float configuredPreview = ConfiguredPreview();
        // The longest observed adjacent mechanism gap is 4.2 seconds.
        // Bound pending work rather than scheduling a whole distant sequence.
        double horizon = Math.Max(configuredPreview, 4.25);
        for(int i=2;i<m.AOEs.Count;i++)
        {
            var a = m.AOEs[i];
            if(a.NextActivation==default) continue;
            if((a.NextActivation-now).TotalSeconds>horizon) continue;
            float preview = i is >=10 and <13 ? Math.Min(configuredPreview,1.5f) : configuredPreview;
            SchedulePrediction(sa, i, a.NextActivation, a.Repeat, preview, now);
        }
    }

    private void SchedulePrediction(ScriptAccessory sa, int index, DateTime activation,
        double cycle, float preview, DateTime now)
    {
        if (index < 0 || index >= drawings.Length) return;
        double remaining = (activation-now).TotalSeconds;
        if (map == null || remaining <= 0 || !double.IsFinite(remaining)) return;
        var previous = drawings[index];
        if (previous.Name != null)
        {
            double correction = Math.Abs((activation-previous.Activation).TotalSeconds);
            // Keep the same visible VFX for this cycle; do not restart its countdown.
            bool sameCycle = correction < Math.Max(0.3, cycle*0.45);
            if (sameCycle && (now >= previous.VisibleAt || correction <= 0.3)) return;
            RetirePrediction(sa, index);
        }
        int totalMs = (int)Math.Min(int.MaxValue, Math.Ceiling(remaining*1000));
        int previewMs = Math.Max(1, (int)(preview*1000));
        int delayMs = Math.Max(0, totalMs-previewMs);
        var a = map.AOEs[index];
        var dp=sa.Data.GetDefaultDrawProperties();
        dp.Name=drawPrefix+"AOE_"+index+"_"+(++drawSequence);
        dp.Position=a.Origin;
        dp.Scale=a.Type==Map.AOEShape.Square?new Vector2(a.R1*2):new Vector2(a.R1);
        dp.Rotation=0; dp.FixRotation=true; dp.Wave=false;
        var color=sa.Data.DefaultDangerColor;
        dp.Color=new Vector4(color.X,color.Y,color.Z,0.80f);
        dp.Delay=delayMs; dp.DestoryAt=Math.Min(totalMs,previewMs);
        dp.ScaleMode=ScaleMode.ByTime;
        sa.Method.SendDraw(DrawModeEnum.Vfx,a.Type==Map.AOEShape.Square?DrawTypeEnum.Straight:DrawTypeEnum.Circle,dp);
        drawings[index]=new Prediction { Name=dp.Name, Activation=activation, VisibleAt=now.AddMilliseconds(delayMs) };
    }

    private void RetirePrediction(ScriptAccessory sa, int index)
    {
        string? name = drawings[index].Name;
        drawings[index] = default;
        if (name != null)
        {
            try { sa.Method.RemoveDraw("^"+name+"$"); }
            catch (Exception ex) { ReportError(sa, ex); }
        }
    }

    private async Task CleanupDrawsAsync(ScriptAccessory sa, string pattern)
    {
        try { sa.Method.RemoveDraw(pattern); }
        catch (Exception ex) { ReportError(sa, ex); }
        // A bounded second pass catches VFX creation already queued on the framework.
        // The old session prefix can never match drawings from a new session.
        try
        {
            await Task.Delay(100).ConfigureAwait(false);
            await sa.Method.RunOnMainThreadAsync(() => sa.Method.RemoveDraw(pattern)).ConfigureAwait(false);
        }
        catch (Exception ex) { ReportError(sa, ex); }
    }

    private void ReportError(ScriptAccessory sa, Exception ex)
    {
        lock (gate)
        {
            var now = DateTime.Now;
            if (now < nextWarning) return;
            nextWarning = now.AddSeconds(5);
            sa.Log.Error("VF3: " + ex);
        }
    }
}
internal static class Service { internal static ScriptAccessory.LogAccessory? Log; }
public sealed class GameEvents
{
    public event Action<uint,Vector3,DateTime>? ActionEffectEvent;
    public event Action<uint,Vector3,DateTime>? StartCastEvent;
    public void Effect(uint id,Vector3 p,DateTime at)=>ActionEffectEvent?.Invoke(id,p,at);
    public void Cast(uint id,Vector3 p,DateTime at)=>StartCastEvent?.Invoke(id,p,at);
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
			return (float)(NextActivation - now).TotalSeconds;
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
protected void UpdateSequence(Vector3 pos, float activateIn, DateTime observed, params AOESequence[] candidates)
	{
		UpdateSequence(pos, activateIn, observed, candidates.AsEnumerable());
	}
protected void UpdateSequence(Vector3 pos, float activateIn, DateTime observed, IEnumerable<AOESequence> candidates)
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
internal static bool MatchesAction(uint actionId, int index, bool cast) =>
    cast ? actionId == 34812 && index is >=21 and <41 :
    actionId switch
    {
        34802 => index is >=0 and <2 or >=10 and <13,
        34801 => index is >=2 and <10 or >=13 and <21 or >=46 and <50 or >=58 and <62,
        34804 or 34812 => index is >=21 and <46,
        34796 => index is >=50 and <58,
        29795 => index is >=62 and <66,
        _ => false
    };

private void OnActionEffect(uint actionId, Vector3 casterPos, DateTime observed)
	{

		switch (actionId)
		{
		case 34801u:
			UpdateSequence(casterPos, 0f, observed, _mech2Exaflares, _mech3Exaflares, _mech4Exaflare, _mech6Exaflare);
			break;
		case 34802u:
			UpdateSequence(casterPos, 0f, observed, _mech1Rotating, _mech3Rotating);
			break;
		case 34804u:
		case 34812u:
			UpdateSequence(casterPos, 0f, observed, _mech4RectsL, _mech4RectsR, _mech4RectsC);
			break;
		case 34796u:
			UpdateSequence(casterPos, 0f, observed, _mech5PairL1, _mech5PairL2, _mech5PairR1, _mech5PairR2);
			break;
		case 29795u:
			UpdateSequence(casterPos, 0f, observed, _mech7PairL, _mech7PairR);
			break;
		}
	}
private void OnStartCast(uint actionId, Vector3 casterPos, DateTime observed)
	{
		if (actionId == 34812)
		{

			UpdateSequence(casterPos, 1f, observed, _mech4RectsL, _mech4RectsR);
		}
	}
private AOESequence CreateRotatingSequence(float repeat, float y, float z, params float[] x)
	{
		return CreateSequence(AOEShape.Circle, 5f, 0f, x.Select((float x2) => (new Vector3(x2, y, z), 0f, repeat: repeat)));
	}
private AOESequence CreateSingleExaflareSequence(float y, float z, params float[] x)
	{
		return CreateSequence(AOEShape.Circle, 6f, 0f, x.Select((xx, i) => (new Vector3(xx, y, z), 0f, i == x.Length - 1 ? 1.9f : 1.4f)));
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
