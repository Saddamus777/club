namespace Club.Protocol;

public static class CheckpointFile
{
    // The caller serializes writers. Readers must allow FileShare.Delete.
    public static void Write(string path, Action<Stream> serialize)
    {
        var temporary = path + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                serialize(file);
                file.Flush(true);
            }
            // ReplaceFile keeps existing readers on the old file. MoveFileEx overwrite can
            // leave the destination delete-pending while a Windows reader still holds it.
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
