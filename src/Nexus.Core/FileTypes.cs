namespace Nexus.Core;

public enum FileCategory { Other, Image, Video, Audio, Document, Archive, Code, Executable }

/// <summary>Extension → category mapping used by the "type:" filter and the Type column.</summary>
public static class FileTypes
{
    private static readonly Dictionary<FileCategory, string[]> Map = new()
    {
        [FileCategory.Image] = ["png", "jpg", "jpeg", "gif", "bmp", "webp", "tif", "tiff", "svg", "ico", "heic", "raw", "psd"],
        [FileCategory.Video] = ["mp4", "mkv", "avi", "mov", "wmv", "flv", "webm", "m4v", "mpg", "mpeg"],
        [FileCategory.Audio] = ["mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus"],
        [FileCategory.Document] = ["pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "txt", "rtf", "odt", "ods", "odp", "md", "csv", "epub"],
        [FileCategory.Archive] = ["zip", "rar", "7z", "tar", "gz", "bz2", "xz", "iso", "cab"],
        [FileCategory.Code] = ["cs", "js", "ts", "py", "java", "c", "cpp", "h", "hpp", "go", "rs", "html", "css", "json", "xml", "yml", "yaml", "sql", "sh", "ps1", "xaml"],
        [FileCategory.Executable] = ["exe", "msi", "dll", "bat", "cmd", "com", "scr"],
    };

    private static readonly Dictionary<string, FileCategory> ByExt =
        Map.SelectMany(kv => kv.Value.Select(e => (e, kv.Key))).ToDictionary(x => x.e, x => x.Key);

    public static IReadOnlyList<string> Extensions(FileCategory c) => Map.TryGetValue(c, out var v) ? v : [];

    public static FileCategory Of(string ext) => ByExt.TryGetValue(ext, out var c) ? c : FileCategory.Other;

    public static bool TryParseCategory(string s, out FileCategory category)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "image": case "images": case "img": case "photo": case "photos": category = FileCategory.Image; return true;
            case "video": case "videos": case "vidéo": case "vidéos": category = FileCategory.Video; return true;
            case "audio": case "music": case "musique": category = FileCategory.Audio; return true;
            case "doc": case "docs": case "document": case "documents": category = FileCategory.Document; return true;
            case "archive": case "archives": case "zip": category = FileCategory.Archive; return true;
            case "code": case "source": category = FileCategory.Code; return true;
            case "exe": case "executable": case "app": case "programme": category = FileCategory.Executable; return true;
            default: category = FileCategory.Other; return false;
        }
    }

    public static string Label(string ext, bool isDirectory)
    {
        if (isDirectory) return "Dossier";
        string cat = Of(ext) switch
        {
            FileCategory.Image => "Image",
            FileCategory.Video => "Vidéo",
            FileCategory.Audio => "Audio",
            FileCategory.Document => "Document",
            FileCategory.Archive => "Archive",
            FileCategory.Code => "Code",
            FileCategory.Executable => "Application",
            _ => "Fichier",
        };
        return ext.Length == 0 ? cat : $"{cat} {ext.ToUpperInvariant()}";
    }
}
