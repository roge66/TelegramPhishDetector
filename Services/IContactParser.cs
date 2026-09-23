using Telegram.Bot.Types;
using TelegramPhishDetector.Models;

namespace TelegramPhishDetector.Services;

public interface IContactParser
{
    ContactInfo Parse(Contact contact);
}