using System.Collections.Concurrent;
using System.Diagnostics;

namespace SpotifyPad;

/// <summary>Polls XInput controllers on a background thread and fires bound actions.</summary>
sealed class Engine : IDisposable
{
    sealed class Compiled
    {
        public uint Mask;
        public PadAction Action;
        // Bindings that contain all of this binding's buttons plus more (e.g. LB+RB+A for LB+RB)
        public Compiled[] Supersets = [];
        public bool Pending;
        public long NextRepeat;
        public long HeldSince;
    }

    sealed class Snapshot(AppConfig config, Compiled[] xbox, Compiled[] playStation)
    {
        public readonly AppConfig Config = config;
        public readonly Compiled[] Xbox = xbox;
        public readonly Compiled[] PlayStation = playStation;
    }

    const int PollMs = 15;
    const int IdlePollMs = 500;
    const int RescanMs = 2000;
    const int RepeatDelayMs = 300;
    // While a volume combo is held, repeats speed up from RepeatStartMs to RepeatMinMs
    const int RepeatStartMs = 200;
    const int RepeatMinMs = 60;
    const int RumbleMs = 70;

    volatile Snapshot snap = new(new AppConfig(), [], []);
    volatile bool running;
    volatile uint pressedXbox, pressedPs;
    volatile uint recordedXbox, recordedPs;
    volatile int connectedXbox, connectedPs;
    volatile string psModel = "";
    readonly PlayStationPads playStation = new();
    Thread? thread;
    Thread? worker;
    // Actions run on a worker so slow calls (DevTools, audio) never stall polling
    readonly BlockingCollection<(PadAction Action, int VolumeDelta, AppConfig Config)> actions = new();
    long rumbleOffAt;

    /// <summary>When true, inputs are tracked but no actions fire (used while editing settings).</summary>
    public volatile bool Suspended;

    public uint Pressed => pressedXbox | pressedPs;
    public uint PressedOn(PadType type) => type == PadType.Xbox ? pressedXbox : pressedPs;
    public int ConnectedCount => connectedXbox + connectedPs;
    public int ConnectedXbox => connectedXbox;
    public int ConnectedPlayStation => connectedPs;
    /// <summary>"DualSense", "DualShock 4" or empty if unknown.</summary>
    public string PlayStationModel => psModel;

    /// <summary>All buttons pressed per controller type since the last <see cref="ResetRecording"/>.</summary>
    public uint RecordedOn(PadType type) => type == PadType.Xbox ? recordedXbox : recordedPs;
    public void ResetRecording() => recordedXbox = recordedPs = 0;

    public void Apply(AppConfig cfg) =>
        snap = new Snapshot(cfg, Compile(cfg.BindingsFor(PadType.Xbox)), Compile(cfg.BindingsFor(PadType.PlayStation)));

    static Compiled[] Compile(List<PadBinding> bindings)
    {
        var list = bindings
            .Where(b => b.Mask != 0)
            .Select(b => new Compiled { Mask = b.Mask, Action = b.Action })
            .ToArray();
        foreach (var c in list)
            c.Supersets = list.Where(o => o.Mask != c.Mask && (o.Mask & c.Mask) == c.Mask).ToArray();
        return list;
    }

    public void Start()
    {
        running = true;
        thread = new Thread(Loop) { IsBackground = true, Name = "PadPoll" };
        thread.Start();
        worker = new Thread(Work) { IsBackground = true, Name = "PadActions" };
        worker.Start();
    }

    void Work()
    {
        foreach (var (action, delta, cfg) in actions.GetConsumingEnumerable())
        {
            try
            {
                SpotifyControl.Execute(action, cfg, delta);
            }
            catch
            {
                // Never let a failed action kill the worker
            }
        }
    }

    void Loop()
    {
        var sw = Stopwatch.StartNew();
        var connected = new bool[4];
        long nextScan = 0;
        uint prev = 0, prevPs = 0;

        while (running)
        {
            long t = sw.ElapsedMilliseconds;
            var s = snap;
            // XInputGetState on empty slots is slow, so only probe them every few seconds
            bool scan = t >= nextScan;
            int count = 0;
            uint now = 0;

            for (int i = 0; i < 4; i++)
            {
                if (!connected[i] && !scan) continue;
                if (XInput.GetState(i, out var st) == 0)
                {
                    connected[i] = true;
                    count++;
                    now |= Map(st.Pad, s.Config.TriggerThreshold);
                }
                else
                {
                    connected[i] = false;
                }
            }
            uint ps = playStation.Poll(scan);
            if (scan) nextScan = t + RescanMs;

            connectedXbox = count;
            connectedPs = playStation.Count;
            psModel = playStation.Model;
            pressedXbox = now;
            pressedPs = ps;
            recordedXbox |= now;
            recordedPs |= ps;

            // Each controller type has its own profile
            if (!Suspended && s.Config.Enabled)
            {
                Process(s.Config, s.Xbox, now, prev, t, connected);
                Process(s.Config, s.PlayStation, ps, prevPs, t, connected);
            }
            else
            {
                foreach (var b in s.Xbox) b.Pending = false;
                foreach (var b in s.PlayStation) b.Pending = false;
            }

            if (rumbleOffAt != 0 && t >= rumbleOffAt)
            {
                SetRumble(connected, 0);
                rumbleOffAt = 0;
            }

            prev = now;
            prevPs = ps;
            Thread.Sleep(count + playStation.Count == 0 ? IdlePollMs : PollMs);
        }
    }

    static uint Map(in XInput.Gamepad pad, int threshold)
    {
        uint m = pad.Buttons;
        if (pad.LeftTrigger >= threshold) m |= (uint)PadButton.LT;
        if (pad.RightTrigger >= threshold) m |= (uint)PadButton.RT;
        return m;
    }

    void Process(AppConfig cfg, Compiled[] bindings, uint now, uint prev, long t, bool[] connected)
    {
        foreach (var b in bindings)
        {
            bool full = (now & b.Mask) == b.Mask;
            bool was = (prev & b.Mask) == b.Mask;

            if (b.Supersets.Length == 0)
            {
                // No bigger combo builds on this one: fire as soon as all buttons are down
                if (full && !was)
                {
                    Fire(cfg, b.Action, 0, connected, t);
                    b.HeldSince = t;
                    b.NextRepeat = t + RepeatDelayMs;
                }
                else if (full && IsRepeatable(b.Action) && t >= b.NextRepeat)
                {
                    Fire(cfg, b.Action, 0, connected, t, repeat: true);
                    long held = t - b.HeldSince - RepeatDelayMs;
                    b.NextRepeat = t + (long)Math.Max(RepeatMinMs, RepeatStartMs - held / 4.0);
                }
                continue;
            }

            // A bigger combo exists (e.g. LB+RB vs LB+RB+A): wait for release,
            // and cancel if the bigger combo gets completed in the meantime
            if (full && !was) b.Pending = true;
            if (!b.Pending) continue;
            foreach (var sup in b.Supersets)
            {
                if ((now & sup.Mask) == sup.Mask)
                {
                    b.Pending = false;
                    break;
                }
            }
            if (b.Pending && !full)
            {
                b.Pending = false;
                Fire(cfg, b.Action, 0, connected, t);
            }
        }
    }

    static bool IsRepeatable(PadAction a) => a is PadAction.VolumeUp or PadAction.VolumeDown;

    void Fire(AppConfig cfg, PadAction action, int volumeDelta, bool[] connected, long t, bool repeat = false)
    {
        // Drop repeats if the worker falls behind, so volume doesn't keep moving after release
        if (!repeat || actions.Count < 2)
            actions.Add((action, volumeDelta, cfg));

        if (!repeat && cfg.Rumble)
        {
            SetRumble(connected, 22000);
            rumbleOffAt = t + RumbleMs;
        }
    }

    static void SetRumble(bool[] connected, ushort strength)
    {
        var v = new XInput.Vibration { Left = strength, Right = strength };
        for (int i = 0; i < 4; i++)
            if (connected[i]) XInput.SetState(i, ref v);
    }

    public void Dispose()
    {
        running = false;
        thread?.Join(1000);
        actions.CompleteAdding();
        SetRumble([true, true, true, true], 0);
    }
}
