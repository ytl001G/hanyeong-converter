using System.Runtime.InteropServices;
using static HanYeong.Native;

namespace HanYeong;

/// <summary>Reads the selected text of the focused app by sending Ctrl+C, then puts the user's clipboard back.</summary>
static class SelectionReader
{
    const ushort VK_CONTROL = 0x11, VK_C = 0x43;

    public static async Task<string?> ReadAsync()
    {
        var saved = Save();
        try
        {
            Clipboard.Clear();
            uint before = GetClipboardSequenceNumber();
            if (!new InputSender().Chord(VK_CONTROL, VK_C).Send()) return null;

            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(15);
                if (GetClipboardSequenceNumber() == before) continue;
                await Task.Delay(15); // let the app finish writing all formats
                return IsEmptySelectionCopy() ? null : Clipboard.GetText().Replace("\r\n", "\n");
            }
            return null;
        }
        catch (ExternalException ex)
        {
            Log.Write($"clipboard busy: {ex.Message}");
            return null;
        }
        finally
        {
            Restore(saved);
        }
    }

    // VS Code copies the whole line when nothing is selected and marks it in its own clipboard format.
    static bool IsEmptySelectionCopy()
    {
        const string VsCodeFormat = "vscode-editor-data";
        if (!Clipboard.ContainsData(VsCodeFormat)) return false;
        return Clipboard.GetData(VsCodeFormat) switch
        {
            string s => s.Contains("\"isFromEmptySelection\":true"),
            MemoryStream ms => System.Text.Encoding.UTF8.GetString(ms.ToArray()).Contains("\"isFromEmptySelection\":true"),
            _ => false,
        };
    }

    static DataObject? Save()
    {
        try
        {
            if (Clipboard.GetDataObject() is not { } source) return null;
            var copy = new DataObject();
            foreach (string format in source.GetFormats(false))
            {
                try
                {
                    if (source.GetData(format, false) is { } data) copy.SetData(format, false, data);
                }
                catch (Exception)
                {
                    // Some formats cannot be read back (delayed rendering, private types); skip them.
                }
            }
            return copy;
        }
        catch (ExternalException)
        {
            return null;
        }
    }

    static void Restore(DataObject? saved)
    {
        try
        {
            if (saved is null || saved.GetFormats().Length == 0) Clipboard.Clear();
            else Clipboard.SetDataObject(saved, copy: true, retryTimes: 5, retryDelay: 50);
        }
        catch (ExternalException ex)
        {
            Log.Write($"clipboard restore failed: {ex.Message}");
        }
    }
}
