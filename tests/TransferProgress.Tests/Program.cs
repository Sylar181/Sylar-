using HarmonyFileManager.Models;
using HarmonyFileManager.Services;

var tracker = new TransferProgressTracker();
TransferProgress Event(string id, long bytes, long total = 100, string state = "running") => new()
{
    TransferId = id, Direction = "download", FileName = "same.txt",
    BytesTransferred = bytes, TotalBytes = total, State = state
};
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine($"PASS {name}");
}

tracker.Reset(200);
tracker.Update(Event("first", 100, state: "done"));
tracker.Update(Event("second", 50));
Check(tracker.DoneWork == 150, "Same-name files use independent IDs");
tracker.Update(Event("first", 100, state: "done"));
Check(tracker.DoneWork == 150, "Repeated completed events do not double count");
tracker.Update(Event("second", 10));
Check(tracker.DoneWork == 110, "Retries discard stale attempt bytes");
tracker.Update(Event("second", 500));
Check(tracker.DoneWork == 200 && tracker.Percent < 100, "Unverified completion stays below 100 percent");
tracker.Update(Event("second", 100, state: "done"));
Check(tracker.Percent == 100, "Verified completion reaches 100 percent");
tracker.Reset(-1);
tracker.Update(Event("unknown", 20, -1));
Check(tracker.Percent == 0 && tracker.DoneWork == 20, "Unknown totals do not fabricate percentages");
tracker.Reset(0);
tracker.Update(Event("empty", 0, 0, "done"));
Check(tracker.DoneWork == 0 && tracker.EtaText == "", "Empty files avoid divide-by-zero and invalid ETA");
tracker.Reset(200);
tracker.Update(Event("next", 20));
Check(tracker.DoneWork == 20, "New batch resets prior state");
