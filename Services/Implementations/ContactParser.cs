using Telegram.Bot.Types;
using TelegramPhishDetector.Models;

namespace TelegramPhishDetector.Services.Implementations;

public class ContactParser : IContactParser
{
    public ContactInfo Parse(Contact contact)
    {
        return new ContactInfo
        {
            FirstName = contact.FirstName,
            LastName = contact.LastName,
            PhoneNumber = contact.PhoneNumber
        };
    }
}