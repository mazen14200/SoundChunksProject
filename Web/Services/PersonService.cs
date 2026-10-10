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
                Id = "Menshawi_Tartil",
                Name = "القارئ الشيخ المنشاوي",
                Description = "صوت عذب و مريح للقلوب (ترتيل) - محمد صديق المنشاوي",
                ImagePath = "images/Sheikh/menshawii3.jpg"
            },
            new Person
            {
                Id = "Menshawi_Talliem",
                Name = "القارئ المنشاوي المُعلم",
                Description = "مقريء القرآن معلم الاطفال - يردد خلفه طفل - محمد صديق المنشاوي",
                ImagePath = "https://via.placeholder.com/70"
            },
            new Person
            {
                Id = "Me3",
                Name = "القارئ الثالث",
                Description = "شيخ القراءات وأحد أعلام القراء",
                ImagePath = "https://via.placeholder.com/70"
            },
            new Person
            {
                Id = "Me4",
                Name = "القارئ الرابع",
                Description = "حفظ القرآن وقرأه بأكثر من رواية",
                ImagePath = "https://via.placeholder.com/70"
            },
            new Person
            {
                Id = "Me5",
                Name = "القارئ الخامس",
                Description = "أحد أشهر قراء العالم الإسلامي",
                ImagePath = "https://via.placeholder.com/70"
            },
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
