
using Microsoft.AspNetCore.Mvc;
using SoundChunksWeb.Models;
using SoundChunksWeb.Services;

namespace SoundChunksWeb.ViewComponents
{
    public class PersonSidebarViewComponent : ViewComponent
    {
        private readonly PersonService _personService;

        public PersonSidebarViewComponent(PersonService personService)
        {
            _personService = personService;
        }

        public IViewComponentResult Invoke()
        {
            var sheikhId = HttpContext.Request.Query["sheikhId"]
                .ToString();

            var persons = _personService.GetAllPersons();

            if (!string.IsNullOrWhiteSpace(sheikhId))
            {
                persons = persons
                    .Where(p => string.Equals(
                        p.Id,
                        sheikhId,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return View(persons);
        }
    }
}