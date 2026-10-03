namespace WinRepair.Core.Safety;

/// <summary>
/// Абстракция инспекции файловой системы для проверки символических ссылок,
/// точек соединения (junctions) и атрибутов файлов без побочных эффектов в Core.
/// </summary>
public interface IFileSystemInspector
{
    /// <summary>
    /// Проверяет, является ли указанный путь символической ссылкой или junction (ReparsePoint).
    /// </summary>
    bool IsReparsePoint(string path);

    /// <summary>
    /// Возвращает конечный (разрешённый) путь для символической ссылки или junction,
    /// либо null, если путь не является ссылкой или не может быть разрешён.
    /// </summary>
    string? ResolveLinkTarget(string path);

    /// <summary>
    /// Проверяет существование файла или каталога.
    /// </summary>
    bool Exists(string path);
}

/// <summary>
/// Стандартная реализация инспектора файловой системы поверх System.IO (.NET 8).
/// </summary>
public sealed class PhysicalFileSystemInspector : IFileSystemInspector
{
    public bool IsReparsePoint(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var attrs = File.GetAttributes(path);
                return (attrs & FileAttributes.ReparsePoint) != 0;
            }

            if (Directory.Exists(path))
            {
                var dirInfo = new DirectoryInfo(path);
                return (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0;
            }

            return false;
        }
        catch
        {
            // При ошибке чтения атрибутов трактуем консервативно в ResolveLinkTarget
            return false;
        }
    }

    public string? ResolveLinkTarget(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                var target = fileInfo.ResolveLinkTarget(returnFinalTarget: true);
                return target?.FullName ?? fileInfo.LinkTarget;
            }

            if (Directory.Exists(path))
            {
                var dirInfo = new DirectoryInfo(path);
                var target = dirInfo.ResolveLinkTarget(returnFinalTarget: true);
                return target?.FullName ?? dirInfo.LinkTarget;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
}
