using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.VisualBasic.FileIO;

namespace Nexus.App.Services;

/// <summary>Shell actions on a result. Methods throw on failure; the caller reports the message.</summary>
public sealed class FileActions
{
    public void Open(string path)
    {
        EnsureExists(path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void OpenLocation(string path)
    {
        EnsureExists(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    public void CopyPath(string path) => Clipboard.SetText(path);

    public void CopyFile(string path)
    {
        EnsureExists(path);
        Clipboard.SetFileDropList(new StringCollection { path });
    }

    public void ShowProperties(string path)
    {
        EnsureExists(path);
        var info = new Native.SHELLEXECUTEINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SHELLEXECUTEINFO>(),
            fMask = Native.SEE_MASK_INVOKEIDLIST,
            lpVerb = "properties",
            lpFile = path,
            nShow = 5,
        };
        if (!Native.ShellExecuteEx(ref info)) throw new IOException("Impossible d'afficher les propriétés.");
    }

    /// <summary>Asks for confirmation, then sends the item to the Recycle Bin (recoverable). Returns false if declined.</summary>
    public bool ConfirmAndDelete(string path, bool isDirectory)
    {
        EnsureExists(path);
        var answer = MessageBox.Show(
            $"Envoyer à la Corbeille ?\n\n{path}", "Supprimer", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return false;
        if (isDirectory) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        return true;
    }

    public bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>Sends one file to the Recycle Bin (recoverable). The caller must already have asked for confirmation.</summary>
    public void SendFileToRecycleBin(string path) =>
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);

    private static void EnsureExists(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("Introuvable (l'index est peut-être obsolète) : " + path);
    }
}
