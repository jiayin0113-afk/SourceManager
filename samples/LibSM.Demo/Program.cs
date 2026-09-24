using SourceManager;

// A minimal embedding of LibSM: no CLI, no ArgumentParser — just the engine.
// Run with: dotnet run --project samples/LibSM.Demo

string dir = Path.Combine(Path.GetTempPath(), "libsm-demo-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(dir);
Console.WriteLine($"repo: {dir}");

var repo = new Repository(dir);
repo.Init("main");
repo.Config.Set("user", "name", "demo");
repo.Config.Set("user", "email", "demo@example.com");

File.WriteAllText(Path.Combine(dir, "hello.txt"), "hello from LibSM\n");
repo.StageFile("hello.txt");
repo.Index.Save();

Hash tree = repo.WriteTreeFromIndex();
Hash commit = repo.CreateCommit(tree, new List<Hash>(), "first commit");
repo.UpdateHead(commit, "commit: first commit");

Console.WriteLine($"HEAD  = {repo.Refs.GetHeadCommit()!.Value.Short}");
Console.WriteLine($"tree  = {tree.Short}");

var read = repo.Objects.ReadCommit(commit);
Console.WriteLine($"read  = {commit.Short} \"{read!.Message}\" parents={read.ParentHashes.Count}");

Hash blob = repo.GetTreeEntries(tree)["hello.txt"];
Console.WriteLine($"blob  = {blob.Short} -> {System.Text.Encoding.UTF8.GetString(repo.Objects.ReadBlob(blob)!).Trim()}");

Console.WriteLine("reflog:");
foreach (ReflogEntry entry in repo.Refs.GetReflog("HEAD"))
    Console.WriteLine($"  {entry.NewHash.Short} {entry.Message} ({entry.Timestamp:u})");

Directory.Delete(dir, recursive: true);
Console.WriteLine("OK");
