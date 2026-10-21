using System.Globalization;
using LUThrift.Web.InputModels;

namespace LUThrift.Web.Pages.Thrift;

public static class CatalogNavigation
{
    public static int ParsePageNumber(string? value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pageNumber)
            ? Math.Max(1, pageNumber) : 1;

    public static Dictionary<string, string> RouteValues(CatalogFilterInput filter, int pageNumber)
    {
        var values = new Dictionary<string, string>();
        if (filter.Search is { } search)
        {
            values["Filter.Search"] = search;
        }
        if (filter.CategoryId is { } categoryId)
        {
            values["Filter.CategoryId"] = categoryId.ToString(CultureInfo.InvariantCulture);
        }
        if (filter.Size is { } size)
        {
            values["Filter.Size"] = size;
        }
        if (filter.Availability != CatalogAvailability.Available)
        {
            values["Filter.Availability"] = ((int)filter.Availability).ToString(CultureInfo.InvariantCulture);
        }
        if (pageNumber > 1)
        {
            values["pageNumber"] = pageNumber.ToString(CultureInfo.InvariantCulture);
        }
        return values;
    }
}
