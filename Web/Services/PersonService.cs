using SoundChunksWeb.Models;

namespace SoundChunksWeb.Services;

public class PersonService
{
    private readonly List<Person> _persons;

    public PersonService()
    {
        _persons = new List<Person>
        {
            new Person
            {
                Name = "القارئ الشيخ المنشاوي",
                Description = "صوت عذب و مريح للقلوب (ترتيل) - محمد صديق المنشاوي",
                ImagePath = "images/Sheikh/menshawii3.jpg"
            },
            new Person
            {
                Name = "القارئ الثاني",
                Description = "مقريء القرآن بخبرة طويلة في التلاوة",
                ImagePath = "https://via.placeholder.com/70"
            },
            new Person
            {
                Name = "القارئ الثالث",
                Description = "شيخ القراءات وأحد أعلام القراء",
                ImagePath = "https://via.placeholder.com/70"
            },
            new Person
            {
                Name = "القارئ الرابع",
                Description = "حفظ القرآن وقرأه بأكثر من رواية",
                ImagePath = "https://via.placeholder.com/70"
            },
            new Person
            {
                Name = "القارئ الخامس",
                Description = "أحد أشهر قراء العالم الإسلامي",
                ImagePath = "https://via.placeholder.com/70"
            },
            new Person
            {
                Name = "القارئ السادس",
                Description = "صاحب أداء رصين وتجويد فريد",
                ImagePath = "https://via.placeholder.com/70"
            }
        };
    }

    public List<Person> GetAllPersons()
    {
        return _persons;
    }

    public Person? GetPersonByName(string name)
    {
        return _persons.FirstOrDefault(p => p.Name == name);
    }
}
