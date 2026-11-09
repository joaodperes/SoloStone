using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

// Per-copy deck entries shared by LocalDecks (sideboards) and LocalProfile (saved decks).
public sealed class LocalDeckCard
{
    public string Id;
    public int Premium;
}

// Mutable ID view for existing Normal-only callers; the entries retain premium per copy.
public sealed class LocalDeckCards : IList<string>
{
    public List<LocalDeckCard> Entries = new List<LocalDeckCard>();
    public static implicit operator LocalDeckCards(List<string> ids) => ids == null ? null
        : new LocalDeckCards { Entries = ids.Select(id => new LocalDeckCard { Id = id }).ToList() };
    public string this[int index] { get => Entries[index]?.Id; set => Entries[index] = new LocalDeckCard { Id = value }; }
    public int Count => Entries.Count;
    public bool IsReadOnly => false;
    public void Add(string id) => Entries.Add(new LocalDeckCard { Id = id });
    public void Clear() => Entries.Clear();
    public bool Contains(string id) => this.Any(value => value == id);
    public int IndexOf(string id) => Entries.FindIndex(entry => entry?.Id == id);
    public void Insert(int index, string id) => Entries.Insert(index, new LocalDeckCard { Id = id });
    public void RemoveAt(int index) => Entries.RemoveAt(index);
    public bool Remove(string id) { int index = IndexOf(id); if (index < 0) return false; RemoveAt(index); return true; }
    public void CopyTo(string[] array, int index) => Entries.Select(entry => entry?.Id).ToArray().CopyTo(array, index);
    public IEnumerator<string> GetEnumerator() => Entries.Select(entry => entry?.Id).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public class LocalDeckCardContainer
{
    [XmlIgnore] public LocalDeckCards Cards = new LocalDeckCards();
    [XmlIgnore] public List<LocalDeckCard> CardEntries {
        get => Cards?.Entries;
        set => Cards = value == null ? null : new LocalDeckCards { Entries = value };
    }
    // Accept the old <string> rows; all new writes carry explicit premium entries.
    [XmlArray("Cards"), XmlArrayItem("string", typeof(string)), XmlArrayItem("Card", typeof(LocalDeckCard))]
    public object[] CardsXml {
        get => CardEntries?.Cast<object>().ToArray();
        set => CardEntries = value?.Select(row => row is string id ? new LocalDeckCard { Id = id } : (LocalDeckCard)row).ToList();
    }
}
