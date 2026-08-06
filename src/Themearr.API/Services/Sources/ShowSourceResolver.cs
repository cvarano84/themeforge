using Themearr.API.Data;

namespace Themearr.API.Services.Sources;

public sealed class ShowSourceResolver(Database db, IEnumerable<IShowLibrarySource> sources)
{
    private readonly IReadOnlyList<IShowLibrarySource> _sources = sources.ToList();

    public IShowLibrarySource Active =>
        Find(db.GetShowLibrarySource()) ?? _sources.First(s => s.Name == "disabled");

    public IShowLibrarySource? Find(string? name) =>
        _sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
}
