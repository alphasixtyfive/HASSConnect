using System.Text;
using System.Xml.Linq;

namespace HassConnect.Core;

public static class NotificationToast
{
    public static string Create(NotificationMessage message, bool sound, string? localImage,
        IReadOnlyList<(string Title, string Argument)> actions)
    {
        var binding = new XElement("binding", new XAttribute("template", "ToastGeneric"));
        if (!string.IsNullOrWhiteSpace(message.Title))
            binding.Add(new XElement("text", Clean(message.Title)));
        binding.Add(new XElement("text", Clean(message.Message)));
        if (localImage is not null)
            binding.Add(new XElement("image", new XAttribute("placement", "hero"), new XAttribute("src", localImage)));
        var toast = new XElement("toast", new XAttribute("launch", "open"), new XElement("visual", binding));
        if (!sound) toast.Add(new XElement("audio", new XAttribute("silent", "true")));
        if (actions.Count > 0)
            toast.Add(new XElement("actions", actions.Select(action => new XElement("action",
                new XAttribute("content", Clean(action.Title)),
                new XAttribute("arguments", Guid.ParseExact(action.Argument, "N").ToString("N")),
                new XAttribute("activationType", "foreground")))));
        return toast.ToString(SaveOptions.DisableFormatting);
    }

    private static string Clean(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value is 9 or 10 or 13 or >= 0x20 and <= 0xD7FF or >= 0xE000 and <= 0xFFFD or >= 0x10000 and <= 0x10FFFF)
                clean.Append(rune.ToString());
        return clean.ToString();
    }
}
