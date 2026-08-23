// The server's own types, under the exact names docs/09-host-contract.md gives.
//
// The plugin reaches them by name through IServiceProvider and never by
// reference, so a test can put types of its own under those names and be the
// server. That is the only way to test this at all: referencing the real
// encoder would make it part of this plugin's ABI, which is the thing the
// contract exists to prevent.

namespace NoMercy.MediaProcessing.Jobs
{
    /// <summary>What queues a job on the real server.</summary>
    public interface IJobDispatcher
    {
        void Dispatch(object job, string queue, int priority);
    }
}

namespace NoMercy.MediaProcessing.Jobs.MediaJobs
{
    /// <summary>The job the dashboard's <em>Add content</em> button dispatches.</summary>
    public sealed class VideoEncodeJob
    {
        /// <summary>
        /// A <c>Ulid</c> on the real server, not a string.
        /// </summary>
        /// <remarks>
        /// Every id on <c>AbstractEncoderJob</c> is one, and the plugin's own
        /// contract carries ids as text — so something has to convert, and
        /// until 23 August 2026 nothing did. Writing a string into it throws,
        /// the dispatch catches it and answers "refused", and no encode was
        /// ever queued. This fake said string, so every test agreed with it.
        /// </remarks>
        public Ulid LibraryId { get; set; }

        public Ulid FolderId { get; set; }

        /// <summary>Looked up with <c>Id.ToInt()</c>, which is why it is a string.</summary>
        public string Id { get; set; } = string.Empty;

        public string InputFile { get; set; } = string.Empty;

        public string? SourceDriverId { get; set; }

        public Ulid? PresetId { get; set; }

        public string QueueName => "encoder";

        public int Priority => 4;
    }
}

namespace NoMercy.Data.Repositories
{
    /// <summary>
    /// The registered one.
    /// </summary>
    /// <remarks>
    /// <c>NoMercy.MediaProcessing.Libraries.ILibraryRepository</c> shares this name
    /// and is not registered on the real server, which is the trap this whole test
    /// exists around.
    /// </remarks>
    public interface ILibraryRepository
    {
        /// <summary>
        /// The one the plugin wants, and it is not alone.
        /// </summary>
        /// <remarks>
        /// The real repository has a second method of this name taking six more
        /// arguments, so asking for it by name alone is ambiguous and throws
        /// before anything is called. The fake had one, so every test passed
        /// while no encode had ever been dispatched on the owner's server.
        /// </remarks>
        Task<object?> GetLibraryByIdAsync(Ulid id);

        Task<object?> GetLibraryByIdAsync(
            Ulid libraryId,
            Guid userId,
            string language,
            string country,
            int take,
            int page,
            CancellationToken ct = default);

        /// <summary>The variant that must not be used: it includes no folders.</summary>
        Task<object?> GetLibraryByIdLiteAsync(Ulid id, CancellationToken ct = default);
    }
}

namespace NoMercy.MediaProcessing.Files
{
    /// <summary>What knows the server's own id for a file on disk.</summary>
    public interface IFileListService
    {
        /// <summary>The two-argument overload, for a folder on this machine.</summary>
        IEnumerable<object> GetFilesInDirectory(string folder, string libraryType);

        /// <summary>The three-argument one, for a folder on a remote share.</summary>
        IEnumerable<object> GetFilesInDirectory(string folder, string libraryType, string storageDriverId);
    }
}
