using System.Text.Json;

namespace Lighthouse.Shell;

/// <summary>Serialises a shell menu tree for the front-end.</summary>
internal static class ShellMenuJson
{
    public static void Write(Utf8JsonWriter w, List<ShellMenuItem> items)
    {
        foreach (var item in items)
        {
            w.WriteStartObject();
            if (item.Separator)
            {
                w.WriteBoolean("sep", true);
            }
            else
            {
                w.WriteNumber("id", item.Id);
                w.WriteString("label", item.Label);
                if (!item.Enabled) w.WriteBoolean("disabled", true);
                if (item.Checked) w.WriteBoolean("checked", true);
                if (item.IsDefault) w.WriteBoolean("default", true);
                if (item.Icon is not null) w.WriteString("icon", item.Icon);
                if (item.Children is { Count: > 0 })
                {
                    w.WriteStartArray("children");
                    Write(w, item.Children);
                    w.WriteEndArray();
                }
            }
            w.WriteEndObject();
        }
    }
}
