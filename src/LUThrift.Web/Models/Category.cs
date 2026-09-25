namespace LUThrift.Web.Models;

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
    public ICollection<Listing> Listings { get; set; } = [];
}
