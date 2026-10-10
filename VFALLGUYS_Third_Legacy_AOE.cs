using KodakkuAssist.Module.Draw;
using KodakkuAssist.Module.GameEvent;
using KodakkuAssist.Script;
using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System;
namespace VF3_Legacy_AOE
{

[ScriptType(name: "糖豆人第三关画图", territorys: [1165], guid: "3a9a6d4c-5f3c-4d6d-9f0d-100000000001", version: "0.2.0.3", author: "MYY", note: "糖豆人第三关画图，基于VFALLGUYS的AOE绘图数据进行编写，纯AI编写。")]
public class VFALLGUYS_Third_Legacy_AOE : IDisposable
{
    private const string Prefix = "VFALLGUYS_Third_Legacy_AOE_";
    private readonly object gate = new();
    private ScriptAccessory? accessory;
    private GameEvents events = new();
    private Map3? map;
    private string? frame;
    private DateTime nextRefresh;
    private DateTime nextWarning;
    private bool[] drawings = [];
    private int drawingCount;
    private string drawPrefix = Prefix;
    private bool visible;
    private const bool ShowUnsynced = true;
    private const float PreviewSeconds = 2.5f;

    public void Init(ScriptAccessory sa)
    {
        Dispose();
        lock (gate)
        {
            accessory = sa; events = new(); nextRefresh = default; nextWarning = default;
            map = new Map3(events);
            drawings = new bool[map.AOEs.Count];
            drawingCount = 0;
            drawPrefix = Prefix + Guid.NewGuid().ToString("N") + "_";
            visible = false;
            frame = sa.Method.RegistFrameworkUpdateAction(Tick, true);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            visible = false;
            
            if (frame != null && accessory != null) accessory.Method.UnregistFrameworkUpdateAction(frame);
            frame = null; map?.Dispose(); map = null;
            accessory?.Method.RemoveDraw("^" + drawPrefix);
            accessory?.Method.ClearFrameworkUpdateAction(this);
            drawings = []; drawingCount = 0; accessory = null;
        }
    }
    [ScriptMethod(name: "第三关机制同步", eventType: EventTypeEnum.ActionEffect, eventCondition: ["ActionId:regex:^(34801|34802|34804|34812|34796|29795)$"], userControl: false)]
    public void Effect(Event e, ScriptAccessory sa) => Observe(e, sa, false);
    [ScriptMethod(name: "方块读条同步", eventType: EventTypeEnum.StartCasting, eventCondition: ["ActionId:34812"], userControl: false)]
    public void Cast(Event e, ScriptAccessory sa) => Observe(e, sa, true);

    private void Observe(Event e, ScriptAccessory sa, bool cast)
    {
        if (!uint.TryParse(e["ActionId"], out var id)) return;
        Vector3 pos;
        try { if (string.IsNullOrWhiteSpace(e["SourcePosition"])) return; pos = JsonConvert.DeserializeObject<Vector3>(e["SourcePosition"]); }
        catch { return; }
        lock (gate)
        {
            if (accessory == null) return;
            if (map == null) return;
            bool matched = false;
            foreach (var a in map.AOEs)
            {
                float dx = pos.X - a.Origin.X, dz = pos.Z - a.Origin.Z;
                if (dx * dx + dz * dz < 2.25f && MathF.Abs(pos.Y - a.Origin.Y) < 2)
                { matched = true; break; }
            }
            if (!matched) return;
            if (cast) events.Cast(id,pos); else events.Effect(id,pos);
        }
    }
    private void Tick()
    {
        lock (gate)
        {
            var sa = accessory; var m = map; var now = DateTime.Now;
            if (sa == null || m == null || now < nextRefresh) return;
            nextRefresh = now.AddMilliseconds(150);
            try
            {
                var player = sa.Data.MyObject;
                if (player == null || player.CurrentHp == 0) { visible=false;  return; }
                var pos = player.Position;
                visible = pos.Z>=123 && pos.Z<=330 && MathF.Abs(pos.X)<=25;
                if (!visible) {  return; }
                if (drawingCount == m.AOEs.Count) return;
                for(int i=0;i<m.AOEs.Count;i++)
                {
                    if (drawings[i]) continue;
                    var a=m.AOEs[i];
                    var dp=sa.Data.GetDefaultDrawProperties(); dp.Name=drawPrefix+"AOE_"+i;
                    dp.Position=a.Origin; dp.Scale=a.Type==Map.AOEShape.Square?new Vector2(a.R1*2):new Vector2(a.R1);
                    dp.Rotation=0; dp.FixRotation=true;
                    dp.Color=new Vector4(0,0,0,0);
                    dp.DestoryAt=int.MaxValue;
                    sa.Method.SendDraw(DrawModeEnum.Default,a.Type==Map.AOEShape.Square?DrawTypeEnum.Straight:DrawTypeEnum.Circle,dp, draw =>
                    {
                        lock(gate)
                        {
                            float t=a.TimeUntilNextActivation(DateTime.Now);
                            var color=sa.Data.DefaultDangerColor;
                            bool synced=a.NextActivation!=default;
                            float alpha=!visible || !ReferenceEquals(map, m)?0:!synced?(ShowUnsynced?0.15f:0):t>PreviewSeconds?0:t<0.6f?1:0.35f;
                            draw.Color=synced?new Vector4(color.X,color.Y,color.Z,alpha):new Vector4(0.65f,0.65f,0.65f,alpha);
                        }
                    });
                    drawings[i] = true;
                    drawingCount++;
                }

            }
            catch(Exception ex)
            {
                
                if(now>=nextWarning) { nextWarning=now.AddSeconds(5); sa.Log.Error("VF3: "+ex.Message); }
            }
        }
    }

}
public sealed class GameEvents
{
    public event Action<uint,Vector3>? ActionEffectEvent;
    public event Action<uint,Vector3>? StartCastEvent;
    public void Effect(uint id,Vector3 p)=>ActionEffectEvent?.Invoke(id,p);
    public void Cast(uint id,Vector3 p)=>StartCastEvent?.Invoke(id,p);
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

		public int LastObservedIndex = -1;
		public DateTime LastObservedAt;
		public int PhaseAnomalies;
		public int[] IntervalSamples = [];
		public float[] PendingIntervals = [];
	}
public readonly List<RepeatingAOE> AOEs = new(66); public virtual void Dispose() {}
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
		for (int i = count; i < AOEs.Count; i++) AOEs[i].Repeat = num;
		return new AOESequence
		{
			StartIndex = count,
			Count = AOEs.Count - count
		};
	}
protected void UpdateSequence(Vector3 pos, float activateIn, AOESequence[] candidates)
    {
        AOESequence? candidate = null;
        int num = -1;
        float distance = float.MaxValue;
        foreach (var sequence in candidates)
        {
            int index = FindIndexInSequence(sequence, pos);
            if (index < 0) continue;
            float d = Vector3.DistanceSquared(AOEs[sequence.StartIndex + index].Origin, pos);
            if (d < distance) { distance = d; candidate = sequence; num = index; }
        }
        if (candidate == null) return;
			DateTime observed = DateTime.Now;
			var current = AOEs[candidate.StartIndex + num];
			double phaseError = current.NextActivation == default ? 0 :
				(observed.AddSeconds(activateIn) - current.NextActivation).TotalSeconds;
			if (current.Repeat > 0) phaseError -= Math.Round(phaseError / current.Repeat) * current.Repeat;
			if (Math.Abs(phaseError) > 0.8 && current.NextActivation != default)
			{
				if (++candidate.PhaseAnomalies < 2) return;
			}
			candidate.PhaseAnomalies = 0;
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
								if(candidate.Count==4 && Math.Abs(adjusted-previous.SeqDelay)>0.25f) {
									bool wholeCycle = true;
                                    float total = 0;
                                    foreach (float value in candidate.PendingIntervals)
                                    { wholeCycle &= value > 0; total += value; }
                                    wholeCycle &= Math.Abs(total - 6.1f) < 0.2f;
									if(wholeCycle) for(int k=0;k<candidate.Count;k++) AOEs[candidate.StartIndex+k].SeqDelay=candidate.PendingIntervals[k];
								} else previous.SeqDelay=adjusted;
								float cycle = 0;
                                for (int k = 0; k < candidate.Count; k++) cycle += AOEs[candidate.StartIndex + k].SeqDelay;
                                for (int k = 0; k < candidate.Count; k++) AOEs[candidate.StartIndex + k].Repeat = cycle;
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
private readonly AOESequence[] exaflares;
private readonly AOESequence[] rotating;
private readonly AOESequence[] rects;
private readonly AOESequence[] pairs;
private readonly AOESequence[] lastPairs;
private readonly AOESequence[] castRects;
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
		_mech6Exaflare = CreateSequence(AOEShape.Circle, 6, 0,
			new float[] { -4, 4, 12, -12 }.Select((x, i) => (new Vector3(x, 33.47f, 145.7f), 0f, i == 1 ? 1.9f : 1.4f)));
		_mech7PairL = CreatePairsSequence(3f, new Vector3(-6.78f, 35.91f, 136.91f), new Vector3(-3.22f, 36.3f, 135.09f));
		_mech7PairR = CreatePairsSequence(3f, new Vector3(6.78f, 35.91f, 136.91f), new Vector3(3.22f, 36.3f, 135.09f));
        exaflares = [_mech2Exaflares, _mech3Exaflares, _mech4Exaflare, _mech6Exaflare];
        rotating = [_mech1Rotating, _mech3Rotating];
        rects = [_mech4RectsL, _mech4RectsR, _mech4RectsC];
        pairs = [_mech5PairL1, _mech5PairL2, _mech5PairR1, _mech5PairR2];
        lastPairs = [_mech7PairL, _mech7PairR];
        castRects = [_mech4RectsL, _mech4RectsR];
	}
private void OnActionEffect(uint actionId, Vector3 casterPos)
	{
		
		switch (actionId)
		{
		case 34801u:
			UpdateSequence(casterPos, 0f, exaflares);
			break;
		case 34802u:
			UpdateSequence(casterPos, 0f, rotating);
			break;
		case 34804u:
		case 34812u:
			UpdateSequence(casterPos, 0f, rects);
			break;
		case 34796u:
			UpdateSequence(casterPos, 0f, pairs);
			break;
		case 29795u:
			UpdateSequence(casterPos, 0f, lastPairs);
			break;
		}
	}
private void OnStartCast(uint actionId, Vector3 casterPos)
	{
		if (actionId == 34812)
		{
			
			UpdateSequence(casterPos, 1f, castRects);
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
