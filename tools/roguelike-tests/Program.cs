using System;
using System.IO;

static partial class Program
{
    private static int count;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        count++;
    }
    private static void Refuses(Action action, string label)
    {
        try { action(); }
        catch (InvalidOperationException) { count++; return; }
        throw new Exception("DID NOT REFUSE: " + label);
    }
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "solostone-dungeon-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RoguelikeTests(root);
            Console.WriteLine("PASS " + count + " dungeon checks");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { Directory.Delete(root, true); }
    }
}
