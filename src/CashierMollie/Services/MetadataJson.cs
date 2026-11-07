using System.Text.Json;

namespace CashierMollie.Services;

/// <summary>
/// The one place metadata is turned into JSON and back.
/// </summary>
/// <remarks>
/// It exists so that the form stored on an order item and the form sent to Mollie cannot drift
/// apart: the column holds exactly the string the payment carries. Two private serialisers would
/// have been the obvious shape and the wrong one -- the stored value would then be correct only
/// for as long as nobody changed one of them.
/// </remarks>
internal static class MetadataJson
{
    /// <summary>
    /// Serialises metadata for storage and for Mollie. Null for an absent or empty dictionary, so
    /// the field is omitted rather than sent as an empty object.
    /// </summary>
    internal static string? Serialise(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is null || metadata.Count == 0
            ? null
            : JsonSerializer.Serialize(metadata);

    /// <summary>
    /// Reads metadata back. Returns false only when there was something to read and it could not
    /// be read -- an absent or empty column is a successful "no metadata", not a failure.
    /// </summary>
    /// <remarks>
    /// The distinction is the point. A caller that cannot tell "nothing stored" from "stored and
    /// unreadable" either logs a warning on every subscription without metadata, or stays silent
    /// when a value it wrote cannot be read back. Only this library writes the column, so the
    /// second case means something outside it changed the row.
    /// </remarks>
    internal static bool TryDeserialise(string? json, out IReadOnlyDictionary<string, string>? metadata)
    {
        metadata = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed is null || parsed.Count == 0)
            {
                return true;
            }

            metadata = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
