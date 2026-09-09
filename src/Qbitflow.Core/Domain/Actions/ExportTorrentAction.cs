using System.Text.Json.Serialization;

namespace Qbitflow.Core.Domain.Actions;

/// <summary>
/// Downloads each matched torrent's <c>.torrent</c> file (qBittorrent's
/// <c>/api/v2/torrents/export</c> endpoint) and writes it into <see cref="DestinationPath"/>
/// on <em>qbitflow's own</em> filesystem -- so that path must exist and be writable from
/// inside the qbitflow container (mount a volume for it), NOT from qBittorrent. This is the
/// one action that touches the local disk rather than only calling the qBittorrent API.
///
/// Files are named "&lt;sanitised torrent name&gt; [&lt;hash&gt;].torrent". Idempotency: skipped
/// for a torrent that already has a "*[&lt;hash&gt;].torrent" file in the target directory --
/// the hash suffix is what's matched, so a torrent renamed since its last export is not
/// re-written.
/// </summary>
public class ExportTorrentAction : ActionDefinition
{
    /// <summary>Directory on qbitflow's filesystem to write the .torrent files into. Created if missing.</summary>
    public required string DestinationPath { get; init; }

    /// <summary>
    /// <see cref="TorrentExportLayout.Flat"/>: every file goes directly in <see cref="DestinationPath"/>.
    /// <see cref="TorrentExportLayout.PerCategory"/>: files go in a "&lt;DestinationPath&gt;/&lt;category&gt;"
    /// sub-directory, with uncategorised torrents left in the root.
    /// </summary>
    public TorrentExportLayout Layout { get; init; } = TorrentExportLayout.Flat;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TorrentExportLayout
{
    Flat,
    PerCategory
}
