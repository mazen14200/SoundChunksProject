using Microsoft.AspNetCore.Mvc;
using SoundChunksWeb.Models;
using SoundChunksWeb.Services;

namespace SoundChunksWeb.ViewComponents;

public class PersonSidebarViewComponent : ViewComponent
{
    private readonly PersonService _personService;

    public PersonSidebarViewComponent(PersonService personService)
    {
        _personService = personService;
    }

    public IViewComponentResult Invoke()
    {
        var persons = _personService.GetAllPersons();
        return View(persons);
    }
}
