using OrchardSound;

public static class MouseSpeedChecks
{
    public static void Run()
    {
        int checks = 0;
        void Check(bool value, string name)
        { if (!value) throw new Exception("FAILED MOUSE SPEED: " + name); checks++; Console.WriteLine("PASS MOUSE SPEED " + name); }
        var backend = new FakeBackend(10);
        using (var scope = new MouseSpeedOverride(backend))
        {
            Check(scope.Update(false, false, 5) && scope.Update(true, false, 5) && backend.Reads == 0 && backend.Writes.Count == 0,
                "disabled or unhovered scope never reads or changes system speed");
            Check(scope.Update(true, true, 5) && backend.Current == 5 && scope.Applied && scope.OriginalSpeed == 10 && scope.LastAppliedSpeed == 5,
                "the first enabled hover captures the existing speed before applying the chosen speed");
            for (int index = 0; index < 20; index++) scope.Update(true, true, 5);
            Check(backend.Writes.SequenceEqual(new[] { 5 }), "steady hover never repeatedly writes the same Windows setting");
            scope.Update(true, true, 7);
            Check(backend.Current == 7 && scope.OriginalSpeed == 10 && scope.LastAppliedSpeed == 7, "live sensitivity changes retain the original pre-hover speed");
            scope.Update(false, true, 7);
            Check(backend.Current == 10 && !scope.Applied && scope.OriginalSpeed == null, "leaving the app restores the original setting and clears ownership");
            int reads = backend.Reads; scope.Update(false, true, 7);
            Check(backend.Reads == reads, "repeated leave notifications do not access Windows after restoration");
            scope.Update(true, true, 4); scope.Update(true, false, 4);
            Check(backend.Current == 10 && !scope.Applied, "disabling a custom sensitivity while hovered restores the original value");
            scope.Update(true, true, 10);
            Check(scope.Applied && scope.OriginalSpeed == 10 && backend.Writes[^1] == 10, "an unchanged requested speed can be scoped without an unnecessary write");
            scope.Update(false, true, 10);
        }
        backend = new(11);
        using (var scope = new MouseSpeedOverride(backend))
        {
            scope.Update(true, true, 3); backend.Current = 16; int writes = backend.Writes.Count;
            Check(scope.Update(true, true, 3) && !scope.Applied && backend.Current == 16 && backend.Writes.Count == writes,
                "an external speed change immediately relinquishes ownership without overwriting it");
            for (int index = 0; index < 10; index++) scope.Update(true, true, 2);
            Check(backend.Current == 16 && backend.Writes.Count == writes, "external changes stay respected for the rest of that hover");
            scope.Update(false, true, 2);
            Check(backend.Current == 16, "leaving after an external change does not restore a stale original speed");
            scope.Update(true, true, 2);
            Check(scope.OriginalSpeed == 16 && backend.Current == 2, "a new hover captures the new external baseline");
        }
        Check(backend.Current == 16, "normal disposal restores the current hover's own baseline");
        backend = new(9); var disposed = new MouseSpeedOverride(backend); disposed.Update(true, true, 4); disposed.Dispose(); int restoredWrites = backend.Writes.Count; disposed.Dispose();
        Check(backend.Current == 9 && backend.Writes.Count == restoredWrites, "normal-close and ProcessExit disposal are idempotent after a successful restore");
        bool rejected = false; try { disposed.Update(true, true, 4); } catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "closed scopes reject further settings updates");
        backend = new(10);
        using (var scope = new MouseSpeedOverride(backend))
        {
            scope.Update(true, true, 5); backend.Current = 19; scope.Dispose();
            Check(backend.Current == 19, "an external change immediately before normal close is retained");
        }
        backend = new(10) { FailRead = true };
        using (var scope = new MouseSpeedOverride(backend))
        {
            Check(!scope.Update(true, true, 5) && scope.LastError == "Synthetic read failure" && !scope.Applied && backend.Writes.Count == 0,
                "a failed native read is reported without applying any setting");
            backend.FailRead = false; backend.FailWrite = true;
            Check(!scope.Update(true, true, 5) && scope.LastError == "Synthetic write failure" && backend.Current == 10,
                "a failed native write returns diagnostic information without throwing from a hover timer");
            backend.FailWrite = false;
            Check(scope.Update(true, true, 5) && scope.LastError == null && backend.Current == 5, "a later successful update clears a transient native error");
            backend.FailWrite = true;
            Check(!scope.Update(false, true, 5) && scope.Applied && backend.Current == 5, "failed restoration retains ownership so a later leave or close can retry");
            backend.FailWrite = false; scope.Update(false, true, 5);
            Check(backend.Current == 10 && !scope.Applied, "a retry completes restoration after a temporary backend failure");
        }
        backend = new(8); var retryDispose = new MouseSpeedOverride(backend); retryDispose.Update(true, true, 4); backend.FailWrite = true; retryDispose.Dispose();
        Check(retryDispose.Applied && retryDispose.LastError != null && backend.Current == 4, "normal close records a failed restore without losing the original baseline");
        backend.FailWrite = false; retryDispose.Dispose();
        Check(backend.Current == 8 && !retryDispose.Applied, "ProcessExit can retry a previously failed close restoration");
        backend = new(0);
        using (var scope = new MouseSpeedOverride(backend))
            Check(!scope.Update(true, true, 5) && backend.Writes.Count == 0 && scope.LastError != null, "invalid values returned by Windows are never captured or applied");
        backend = new(10);
        using (var scope = new MouseSpeedOverride(backend))
        {
            rejected = false; try { scope.Update(true, true, 0); } catch (ArgumentOutOfRangeException) { rejected = true; }
            Check(rejected && backend.Reads == 0, "out-of-range desired values are rejected before any backend operation");
        }
        Console.WriteLine($"{checks} mouse speed checks passed. All Windows operations were replaced by an in-memory fake.");
    }
    sealed class FakeBackend(int speed) : IMouseSpeedBackend
    {
        public int Current = speed, Reads;
        public bool FailRead, FailWrite;
        public List<int> Writes { get; } = [];
        public int Read() { Reads++; if (FailRead) throw new IOException("Synthetic read failure"); return Current; }
        public void Write(int value) { if (FailWrite) throw new IOException("Synthetic write failure"); Current = value; Writes.Add(value); }
    }
}
