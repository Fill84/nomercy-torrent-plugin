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
        public string LibraryId { get; set; } = string.Empty;

        public string FolderId { get; set; } = string.Empty;

        /// <summary>Looked up with <c>Id.ToInt()</c>, which is why it is a string.</summary>
        public string Id { get; set; } = string.Empty;

        public string InputFile { get; set; } = string.Empty;

        public string? SourceDriverId { get; set; }

        public string? PresetId { get; set; }

        public string QueueName => "encoder";

        public int Priority => 5;
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
        Task<object?> GetLibraryByIdAsync(string libraryId);

        /// <summary>The variant that must not be used: it includes no folders.</summary>
        Task<object?> GetLibraryByIdLiteAsync(string libraryId);
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
