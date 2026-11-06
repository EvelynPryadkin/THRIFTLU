using LUThrift.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LUThrift.Web.Pages.Thrift;

// Every image GET goes through current listing visibility and staff eligibility.
// Do not cache a response that could outlive publishing, removal, or role changes.
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public class ImageModel(ImageService images) : PageModel
{
    public async Task<IActionResult> OnGetAsync([FromRoute] int id, CancellationToken cancellationToken)
    {
        Response.Headers.XContentTypeOptions = "nosniff";
        var image = await images.OpenReadAsync(id, User, cancellationToken);
        if (image is null) return NotFound();

        // FileStreamResult disposes the stream. Neither the storage path nor a
        // download filename is exposed, and no ETag or Last-Modified is supplied.
        return File(image.Content, image.ContentType);
    }
}
