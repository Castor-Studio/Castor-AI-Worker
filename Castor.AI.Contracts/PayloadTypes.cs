namespace Castor.AI.Contracts;

/// <summary>
/// Known <c>PayloadEvent.type</c> values. Data fields are documented per type.
/// </summary>
public static class PayloadTypes
{
    /// <summary>A person starts talking. Data: <c>camera_id</c> (camera tied to that person).</summary>
    public const string SpeakerStart = "speaker.start";

    /// <summary>A person stops talking. Data: <c>camera_id</c>.</summary>
    public const string SpeakerStop = "speaker.stop";

    /// <summary>
    /// Per-camera hints merged into the camera state. Data: <c>camera_id</c> plus any of
    /// <c>is_speaking</c> (bool), <c>volume_db</c> (number), <c>face_detected</c> (bool),
    /// <c>person_detected</c> (bool). A null value clears the field.
    /// </summary>
    public const string CameraMetadata = "camera.metadata";
}
