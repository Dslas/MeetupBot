using System;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using System.Collections.Generic;
using System.Linq;
using Telegram.Bot.Polling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Data;
using System.Data.SqlClient;
using Newtonsoft.Json;
using System.IO;

namespace MeetupBot
{
    class Program
    {
        private static TelegramBotClient _botClient; // Клиент для работы с Telegram Bot API.
        private static long _adminChatId; // ID чата администратора.
        private static Dictionary<long, MeetupDetails> _meetups = new Dictionary<long, MeetupDetails>(); // Словарь для хранения митапов.
        private static IConfiguration _configuration; // Объект для работы с конфигурацией.
        private static Timer _notificationTimer; // Таймер для отправки уведомлений.
        private static string _connectionString; // Строка подключения к базе данных.
        private static MeetupNotificationSettings _meetupSettings; // Настройки уведомлений о 

        static async Task Main(string[] args) // Главный метод программы, который запускает инициализацию.
        {
            var program = new Program();
            await program.InitializeAsync(args);
        }

        private async Task InitializeAsync(string[] args)  // Метод для инициализации бота и загрузки необходимых настроек.
        {
            LoadConfiguration(args);
            _botClient = new TelegramBotClient(_configuration["TelegramBotToken"]);
            await InitializeBot();
            await LoadSettingsFromDatabase();
            StartMeetupTimers();
            await StartReceivingUpdates();
            await CommandLoop();
        }

        private async Task InitializeBot() // Метод для инициализации бота и проверки его работоспособности.
        {
            try
            {
                var me = await _botClient.GetMeAsync();
                Console.WriteLine($"Bot started: {me.Username}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при получении информации о боте: {ex.Message}");
                Environment.Exit(1);
            }
        }

        private void LoadConfiguration(string[] args)  // Метод для загрузки конфигурации из файла appsettings.json.
        {
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((hostingContext, configuration) =>
                {
                    configuration.Sources.Clear();
                    IHostEnvironment env = hostingContext.HostingEnvironment;
                    configuration
                        .SetBasePath(env.ContentRootPath)
                        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
                })
                .Build();

            _configuration = host.Services.GetRequiredService<IConfiguration>();
            _connectionString = _configuration.GetConnectionString("DefaultConnection");
            _adminChatId = _configuration.GetValue<long>("MeetupNotifications:AdminChatId");
        }

        private async Task LoadSettingsFromDatabase() // Метод для загрузки настроек уведомлений из базы данных.
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                var command = new SqlCommand("SELECT TOP 1 NotificationTime, PreNotificationTime, IsEnabled FROM MeetupNotificationSettings", connection);
                using (var reader = await command.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        _meetupSettings = new MeetupNotificationSettings
                        {
                            NotificationTime = reader.GetTimeSpan(0),
                            PreNotificationTime = reader.GetTimeSpan(1),
                            IsEnabled = reader.GetBoolean(2)
                        };
                    }
                }
            }
        }

        private async Task SaveSettingsToDatabase() // Метод для сохранения настроек уведомлений в базе данных
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                var command = new SqlCommand("UPDATE MeetupNotificationSettings SET NotificationTime = @NotificationTime, PreNotificationTime = @PreNotificationTime, IsEnabled = @IsEnabled", connection);
                command.Parameters.AddWithValue("@NotificationTime", _meetupSettings.NotificationTime);
                command.Parameters.AddWithValue("@PreNotificationTime", _meetupSettings.PreNotificationTime);
                command.Parameters.AddWithValue("@IsEnabled", _meetupSettings.IsEnabled);
                await command.ExecuteNonQueryAsync();
            }
        }

        private static void SaveSettings() // Метод для сохранения конфигурации в файл appsettings.json
        {
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            var section = configuration.GetSection("MeetupNotifications");
            section["AdminChatId"] = _adminChatId.ToString();

            var updatedSettings = new
            {
                TelegramBotToken = section["TelegramBotToken"],
                MeetupNotifications = new
                {
                    AdminChatId = section["AdminChatId"],
                },
                ConnectionStrings = new
                {
                    DefaultConnection = configuration.GetConnectionString("DefaultConnection")
                }
            };

            System.IO.File.WriteAllText("appsettings.json", JsonConvert.SerializeObject(updatedSettings, Formatting.Indented));
        }

        private void StartMeetupTimers() // Метод для запуска таймеров уведомлений о митапах. Если уведомления включены, устанавливается таймер для отправки уведомлений каждую минуту.
        {
            if (_meetupSettings.IsEnabled)
            {
                _notificationTimer = new Timer(SendNotifications, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
            }
        }

        private void SendNotifications(object state) // Метод для отправки уведомлений о предстоящих митапах. Проверяет текущее время и отправляет уведомления администратору.
        {
            var currentTime = DateTime.Now;

            if (currentTime.DayOfWeek == DayOfWeek.Saturday || currentTime.DayOfWeek == DayOfWeek.Sunday)
            {
                return;
            }

            if (!_meetupSettings.IsEnabled)
            {
                return;
            }

            var preNotificationTime = _meetupSettings.NotificationTime - _meetupSettings.PreNotificationTime;
            if (currentTime.TimeOfDay >= preNotificationTime && currentTime.TimeOfDay < _meetupSettings.NotificationTime)
            {
                var preNotificationMessage = $"Напоминание: митап начнется через {_meetupSettings.PreNotificationTime:hh\\:mm}!";
                _botClient.SendTextMessageAsync(_adminChatId, preNotificationMessage).Wait();
            }

            if (currentTime.TimeOfDay >= _meetupSettings.NotificationTime &&
                currentTime.TimeOfDay < _meetupSettings.NotificationTime + TimeSpan.FromMinutes(1))
            {
                var notificationMessage = "Митап начинается прямо сейчас!";
                _botClient.SendTextMessageAsync(_adminChatId, notificationMessage).Wait();
            }
        }

        private async Task StartReceivingUpdates() // Метод для начала получения обновлений от Telegram. Запускает обработчик обновлений с использованием токена отмены.
        {
            using var cts = new CancellationTokenSource();
            _botClient.StartReceiving(
                new DefaultUpdateHandler(HandleUpdateAsync, HandleErrorAsync),
                receiverOptions: null,
                cancellationToken: cts.Token);
        }

        private async Task CommandLoop() // Метод для обработки команд, вводимых в консоль. Позволяет администратору управлять настройками бота.
        {
            while (true)
            {
                Console.WriteLine("Введите команду: ");
                var command = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(command)) continue;

                if (command.StartsWith("setadmin"))
                {
                    var parts = command.Split(' ');
                    if (parts.Length > 1 && long.TryParse(parts[1], out var chatId))
                    {
                        SetAdminChatId(chatId);
                    }
                    else
                    {
                        Console.WriteLine("Некорректный формат команды. Используйте: setadmin <chat_id>");
                    }
                }

                if (command.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }
        }

        private void SetAdminChatId(long chatId) // Метод для установки идентификатора чата администратора и сохранения настроек.
        {
            _adminChatId = chatId;
            SaveSettings();
            Console.WriteLine($"Admin Chat ID установлен на: {_adminChatId}");
        }

        private async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken) // Метод для обработки обновлений от Telegram. Проверяет тип обновления и вызывает соответствующий обработчик.
        {
            if (update.Type != UpdateType.Message || update.Message.Type != MessageType.Text)
                return;

            var message = update.Message;

            if (message != null && message.Chat.Id == _adminChatId)
            {
                await ProcessAdminCommands(botClient, message);
            }
        }

        private async Task ProcessAdminCommands(ITelegramBotClient botClient, Message message) // Метод для обработки команд, отправленных администратором. Выполняет соответствующие действия в зависимости от команды.
        {
            switch (message.Text)
            {
                case "/startmeetup": // Команда для начала митапа
                    await StartMeetup(botClient, message);
                    break;
                case "/endmeetup": // Команда для окончания митапа
                    await EndMeetup(botClient, message);
                    break;
                case "/enable_notifications": // Команда для включения уведомлений
                    _meetupSettings.IsEnabled = true;
                    await botClient.SendTextMessageAsync(message.Chat, "Уведомления включены!");
                    await SaveSettingsToDatabase();
                    break;
                case "/disable_notifications": // Команда для выключения уведомлений
                    _meetupSettings.IsEnabled = false;
                    await botClient.SendTextMessageAsync(message.Chat, "Уведомления отключены!");
                    await SaveSettingsToDatabase();
                    break;
                case var cmd when cmd.StartsWith("/set_pre_notification_time"): // Команда для установки уведомлений перед началом митапа
                    await SetPreNotificationTime(botClient, message);
                    break;
                case var cmd when cmd.StartsWith("/set_notification_time"): // Команда для установки ежедневных уведомлений
                    await SetNotificationTime(botClient, message);
                    break;
                case "/help": // Команда для вывода всех существующих комад
                    await SendHelpMessage(botClient, message);
                    break;
                case "/About": // Команда для вывода информации о разработчике
                    await SendAboutMessage(botClient, message);
                    break;
                default: 
                    await botClient.SendTextMessageAsync(message.Chat, "Неизвестная команда."); 
                    break;
            }
        }

        private async Task SetPreNotificationTime(ITelegramBotClient botClient, Message message) // Метод для установки времени предварительного уведомления. Проверяет формат времени и сохраняет его.
        {
            var parts = message.Text.Split(' ');
            if (parts.Length == 2 && TimeSpan.TryParse(parts[1], out var newTime))
            {
                _meetupSettings.PreNotificationTime = newTime;
                await SaveSettingsToDatabase();
                await botClient.SendTextMessageAsync(message.Chat, $"Время предварительного уведомления установлено на {newTime}");
            }
            else
            {
                await botClient.SendTextMessageAsync(message.Chat, "Неверный формат времени. Используйте HH:mm:ss.");
            }
        }

        private async Task SetNotificationTime(ITelegramBotClient botClient, Message message) // Метод для установки времени уведомления.
        {
            var parts = message.Text.Split(' ');
            if (parts.Length == 2 && TimeSpan.TryParse(parts[1], out var newNotificationTime))
            {
                _meetupSettings.NotificationTime = newNotificationTime;
                await SaveSettingsToDatabase();
                await botClient.SendTextMessageAsync(message.Chat, $"Время уведомления установлено на {newNotificationTime}");
            }
            else
            {
                await botClient.SendTextMessageAsync(message.Chat, "Неверный формат времени. Используйте HH:mm:ss.");
            }
        }

        private async Task SendHelpMessage(ITelegramBotClient botClient, Message message) // Метод для отправки списка доступных команд администратору.
        {
            var helpMessage = "Доступные команды:\n" +
            "/startmeetup - Начать митап.\n" +
            "/endmeetup - Завершить митап.\n" +
            "/enable_notifications - Включить уведомления.\n" +
            "/disable_notifications - Отключить уведомления.\n" +
            "/set_pre_notification_time HH:mm:ss - Установить время предварительного уведомления.\n" +
            "/set_notification_time HH:mm:ss - Установить новое время уведомлений.\n" +
            "/About - Узнать о программе.";
            await botClient.SendTextMessageAsync(message.Chat, helpMessage);
        }

        private async Task SendAboutMessage(ITelegramBotClient botClient, Message message) // Метод для отправки информации о разработчике и версии программы администратору.
        {
            var aboutMessage = "Разработал - Печёнкин Д.В:\n" +
            "Обновлено: Версия 2.0 : 05:11:2024";
            await botClient.SendTextMessageAsync(message.Chat, aboutMessage);
        }

        private async Task StartMeetup(ITelegramBotClient botClient, Message message) // Метод для начала нового митапа. Создает запись о митапе и уведомляет администратора о начале.
        {
            var meetupId = DateTime.Now.Ticks;
            _meetups[meetupId] = new MeetupDetails
            {
                StartTime = DateTime.Now,
                Participants = new List<long>()
            };
            await botClient.SendTextMessageAsync(message.Chat, "Митап начался!");
        }

        private async Task EndMeetup(ITelegramBotClient botClient, Message message) // Метод для завершения текущего митапа. Устанавливает время окончания и сохраняет данные в базе.
        {
            var meetupId = _meetups.Keys.LastOrDefault();
            if (meetupId != 0)
            {
                var meetup = _meetups[meetupId];
                meetup.EndTime = DateTime.Now;
                await botClient.SendTextMessageAsync(message.Chat, "Митап завершен!");

                await SaveMeetupToDatabase(meetupId, meetup);
                _meetups.Remove(meetupId);
            }
        }

        private async Task SaveMeetupToDatabase(long meetupId, MeetupDetails meetup) // Метод для сохранения данных о митапе в базе данных. Использует SQL-запрос для вставки информации.
        {
            using (var connection = new SqlConnection(_connectionString))
            {
                var command = new SqlCommand("INSERT INTO Meetups (MeetupId, StartTime, EndTime) VALUES (@MeetupId, @StartTime, @EndTime)", connection);
                command.Parameters.AddWithValue("@MeetupId", meetupId);
                command.Parameters.AddWithValue("@StartTime", meetup.StartTime);
                command.Parameters.AddWithValue("@EndTime", meetup.EndTime);
                await connection.OpenAsync();
                await command.ExecuteNonQueryAsync();
            }
        }

        private Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken) // Метод для обработки ошибок, возникающих при работе с Telegram API. Выводит информацию об ошибке в консоль.
        {
            Console.WriteLine(exception.ToString());
            return Task.CompletedTask;
        }
    }

    class MeetupDetails // Класс для хранения информации о митапе, включая время начала, окончания и участников.
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public List<long> Participants { get; set; } = new List<long>();
    }

    public class MeetupNotificationSettings // Класс для хранения настроек уведомлений о митапах, включая время уведомлений и статус включения.
    {
        public int Id { get; set; }
        public TimeSpan NotificationTime { get; set; }
        public TimeSpan PreNotificationTime { get; set; }
        public bool IsEnabled { get; set; }
    }
}
