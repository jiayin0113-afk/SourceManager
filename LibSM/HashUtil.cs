namespace SourceManager;

public static class HashUtil
{
    public static Hash ComputeBlob(byte[] content)
    {
        string header = $"blob {content.Length}\0";
        byte[] headerBytes = System.Text.Encoding.UTF8.GetBytes(header);
        byte[] combined = new byte[headerBytes.Length + content.Length];
        Array.Copy(headerBytes, 0, combined, 0, headerBytes.Length);
        Array.Copy(content, 0, combined, headerBytes.Length, content.Length);
        return Hash.Compute(combined);
    }

    public static Hash ComputeTree(byte[] serializedTree)
    {
        string header = $"tree {serializedTree.Length}\0";
        byte[] headerBytes = System.Text.Encoding.UTF8.GetBytes(header);
        byte[] combined = new byte[headerBytes.Length + serializedTree.Length];
        Array.Copy(headerBytes, 0, combined, 0, headerBytes.Length);
        Array.Copy(serializedTree, 0, combined, headerBytes.Length, serializedTree.Length);
        return Hash.Compute(combined);
    }

    public static Hash ComputeCommit(byte[] serializedCommit)
    {
        string header = $"commit {serializedCommit.Length}\0";
        byte[] headerBytes = System.Text.Encoding.UTF8.GetBytes(header);
        byte[] combined = new byte[headerBytes.Length + serializedCommit.Length];
        Array.Copy(headerBytes, 0, combined, 0, headerBytes.Length);
        Array.Copy(serializedCommit, 0, combined, headerBytes.Length, serializedCommit.Length);
        return Hash.Compute(combined);
    }

    public static Hash ComputeTag(byte[] serializedTag)
    {
        string header = $"tag {serializedTag.Length}\0";
        byte[] headerBytes = System.Text.Encoding.UTF8.GetBytes(header);
        byte[] combined = new byte[headerBytes.Length + serializedTag.Length];
        Array.Copy(headerBytes, 0, combined, 0, headerBytes.Length);
        Array.Copy(serializedTag, 0, combined, headerBytes.Length, serializedTag.Length);
        return Hash.Compute(combined);
    }

    public static bool VerifyObject(ObjectType type, byte[] data, Hash expectedHash)
    {
        Hash actual = type switch
        {
            ObjectType.Blob => ComputeBlob(data),
            ObjectType.Tree => ComputeTree(data),
            ObjectType.Commit => ComputeCommit(data),
            ObjectType.Tag => ComputeTag(data),
            _ => Hash.Zero
        };
        return actual.Equals(expectedHash);
    }

    public static Hash HashFile(string path)
    {
        byte[] content = File.ReadAllBytes(path);
        return ComputeBlob(content);
    }
}