using System.Text.RegularExpressions;
using Castor.AI.Contracts.V1;

namespace Castor.AI.Worker.Strategies.Podcast;

/// <summary>
/// Maps cameras to director roles: <c>host</c>, <c>guest</c>, <c>guest2</c>..., <c>wide</c>,
/// <c>&lt;speaker&gt;_zoom</c>. Explicit <see cref="CameraDescriptor.Role"/> wins; cameras without
/// a recognized role go through the label heuristics of the Python <c>PodcastModule._parse_roles</c>.
/// </summary>
public static partial class PodcastRoles
{
    public const string Wide = "wide";
    public const string Host = "host";
    public const string Guest = "guest";
    public const string ZoomSuffix = "_zoom";

    private static readonly string[] HostKeywords = ["hote", "host", "presentateur", "presentatrice", "cam 1", "cam1"];
    private static readonly string[] GuestKeywords = ["invite", "invité", "guest", "intervenant", "panelist", "panéliste"];
    private static readonly string[] WideKeywords = ["large", "wide", "plan", "studio"];
    private static readonly string[] ZoomKeywords = ["zoom", "serre", "serré", "tight", "face"];

    /// <summary>Speaker roles carry their own mic; wide and zooms reuse another role's audio.</summary>
    public static bool IsSpeakerRole(string role) => role != Wide && !role.EndsWith(ZoomSuffix, StringComparison.Ordinal);

    /// <summary>
    /// Speaker role from a camera label: "host", "guest" (first/only guest), "guest2", ...
    /// Null when the label does not look like a speaker camera (e.g. a wide shot).
    /// </summary>
    public static string? ClassifySpeakerLabel(string label)
    {
        var text = label.ToLowerInvariant();

        if (ContainsAny(text, HostKeywords))
        {
            return Host;
        }

        var match = NumberedGuest().Match(text);
        if (match.Success)
        {
            var fromCam = !match.Groups[1].Success;
            var number = int.Parse(fromCam ? match.Groups[2].Value : match.Groups[1].Value);
            var guestIndex = fromCam ? number - 1 : number; // bare "camN" is offset by the host's cam1
            return guestIndex <= 1 ? Guest : $"{Guest}{guestIndex}";
        }

        return ContainsAny(text, GuestKeywords) ? Guest : null;
    }

    /// <summary>role -> camera_id, in assignment order.</summary>
    public static Dictionary<string, string> Resolve(IReadOnlyList<CameraDescriptor> cameras)
    {
        var roles = new Dictionary<string, string>();
        var matched = new HashSet<string>();

        void Assign(string role, CameraDescriptor camera)
        {
            roles[role] = camera.CameraId;
            matched.Add(camera.CameraId);
        }

        // 0. Explicit roles from the app.
        foreach (var camera in cameras)
        {
            if (NormalizeExplicit(camera.Role) is { } role && !roles.ContainsKey(role))
            {
                Assign(role, camera);
            }
        }

        // 1. Wide shot.
        if (!roles.ContainsKey(Wide))
        {
            foreach (var camera in cameras)
            {
                if (!matched.Contains(camera.CameraId) && ContainsAny(camera.Label.ToLowerInvariant(), WideKeywords))
                {
                    Assign(Wide, camera);
                    break;
                }
            }
        }

        // 2. Zooms (per speaker).
        foreach (var camera in cameras)
        {
            if (matched.Contains(camera.CameraId) || !ContainsAny(camera.Label.ToLowerInvariant(), ZoomKeywords))
            {
                continue;
            }

            if (ClassifySpeakerLabel(camera.Label) is { } baseRole && !roles.ContainsKey(baseRole + ZoomSuffix))
            {
                Assign(baseRole + ZoomSuffix, camera);
            }
        }

        // 3. Regular speaker cameras.
        foreach (var camera in cameras)
        {
            if (!matched.Contains(camera.CameraId)
                && ClassifySpeakerLabel(camera.Label) is { } baseRole
                && !roles.ContainsKey(baseRole))
            {
                Assign(baseRole, camera);
            }
        }

        // 4. Index-based fallback for the rest.
        var unmatched = cameras.Where(c => !matched.Contains(c.CameraId)).ToList();

        // "3rd unlabeled camera is the wide shot" only fits the classic host/guest/wide rig.
        if (!roles.ContainsKey(Wide) && cameras.Count == 3 && unmatched.Count > 0)
        {
            var candidate = cameras[2];
            if (unmatched.Remove(candidate))
            {
                Assign(Wide, candidate);
            }
        }

        if (!roles.ContainsKey(Host) && unmatched.Count > 0)
        {
            Assign(Host, Pop(unmatched));
        }

        if (!roles.ContainsKey(Guest) && unmatched.Count > 0)
        {
            Assign(Guest, Pop(unmatched));
        }

        var extraGuest = 2;
        while (unmatched.Count > 0)
        {
            var role = $"{Guest}{extraGuest}";
            if (!roles.ContainsKey(role))
            {
                Assign(role, Pop(unmatched));
            }

            extraGuest++;
        }

        if (!roles.ContainsKey(Wide) && cameras.Count >= 1)
        {
            roles[Wide] = cameras[^1].CameraId;
        }

        return roles;
    }

    private static string? NormalizeExplicit(string role)
    {
        var text = role.Trim().ToLowerInvariant();
        if (!ExplicitRole().IsMatch(text))
        {
            return null;
        }

        // "guest1" is the first guest, same as "guest".
        return text switch
        {
            "guest1" => Guest,
            "guest1_zoom" => Guest + ZoomSuffix,
            _ => text,
        };
    }

    private static CameraDescriptor Pop(List<CameraDescriptor> list)
    {
        var first = list[0];
        list.RemoveAt(0);
        return first;
    }

    private static bool ContainsAny(string text, string[] keywords) => keywords.Any(text.Contains);

    [GeneratedRegex(@"(?:invite|invité|guest|intervenant|panelist|pan[ée]liste)\D*(\d+)|cam\s*(\d+)")]
    private static partial Regex NumberedGuest();

    [GeneratedRegex(@"^(?:wide|(?:host|guest\d*)(?:_zoom)?)$")]
    private static partial Regex ExplicitRole();
}
