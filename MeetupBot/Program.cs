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
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Data; 
using System.Data.SqlClient; 
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.NamingConventionBinder;
using Newtonsoft.Json;


namespace MeetupBot
{
    class Program
    {
        private static TelegramBotClient _botClient;
        private static long _adminChatId;
        private static Dictionary<long, MeetupDetails> _meetups = new Dictionary<long, MeetupDetails>();
        private static IConfiguration _configuration;
        private static Timer _notificationTimer;
        private static string _connectionString; // Строка подключения к БД
        private static bool _notificationsEnabled = true; // По умолчанию уведомления включены
        private static TimeSpan _preNotificationTime = TimeSpan.FromMinutes(15); // Время предварительного уведомления
        private static MeetupNotificationSettings _meetupSettings; // Добавьте поле в класс Program для хранения настроек

        static async Task Main(string[] args)
        {
            _botClient = new TelegramBotClient("7332920725:AAGmIeIR0a2r9gFSQ0Z911CB-RIBQK-DVVg");
            var me = await _botClient.GetMeAsync();
            Console.WriteLine($"Bot started: {me.Username}");

            LoadConfiguration(args); // Исправлено: args передаются в метод
            StartMeetupTimers();

            using var cts = new CancellationTokenSource();
            _botClient.StartReceiving(
                new DefaultUpdateHandler(HandleUpdateAsync, HandleErrorAsync),
                receiverOptions: null,
                cancellationToken: cts.Token);

            // Создание корневой команды и добавление команды setadmin
            var rootCommand = new RootCommand();
            var setAdminCommand = new Command("setadmin", "Устанавливает администратора по chat-id");
            setAdminCommand.AddArgument(new Argument<long>("chat-id", "Chat ID администратора"));

            // Установка обработчика для команды setadmin

            setAdminCommand.SetHandler((long AdminChatId) =>
            {
                _adminChatId = AdminChatId;
                SaveSettings();
                Console.WriteLine($"Администратор установлен: {_adminChatId}");
                // Здесь можно добавить код для сохранения _adminChatId в конфигурацию или базу данных
            }, new Argument<long>("chat-id"));

            rootCommand.AddCommand(setAdminCommand);

            // Обработка аргументов командной строки и запуск приложения
            await rootCommand.InvokeAsync(args);

            while (true)
            {
                Console.WriteLine("Введите команду:");
                var input = Console.ReadLine();
                if (input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                await rootCommand.InvokeAsync(new string[] { input });
            }

            cts.Cancel();
        }

        static void LoadConfiguration(string[] args) 
        {
            var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((hostingContext, configuration) =>
            {
                 configuration.Sources.Clear();
                 IHostEnvironment env = hostingContext.HostingEnvironment;
                 configuration
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
                .Build();

            _configuration = host.Services.GetRequiredService<IConfiguration>();
            _connectionString = _configuration.GetConnectionString("DefaultConnection");     
            _meetupSettings = _configuration.GetSection("MeetupNotifications").Get<MeetupNotificationSettings>();
            _adminChatId = _configuration.GetValue<long>("MeetupNotifications:AdminChatId"); // Загрузка AdminChatId
        }

        static void ConfigureNotifications()
        {
            // Загрузка настроек уведомлений из конфигурации
            _notificationsEnabled = _configuration.GetValue<bool>("NotificationsEnabled", true);
            _preNotificationTime = _configuration.GetValue<TimeSpan>("PreNotificationTime", TimeSpan.FromMinutes(15));
        }

        static void StartMeetupTimers()
        {
            if (_notificationsEnabled)
            {               
                _notificationTimer = new Timer(SendNotifications, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
            }
        }

        static void SendNotifications(object state)
        {
            var currentTime = DateTime.Now;

            // Проверка, не выходной ли сегодня
            if (currentTime.DayOfWeek == DayOfWeek.Saturday || currentTime.DayOfWeek == DayOfWeek.Sunday)
            {
                return; 
            }

            // Проверка, включены ли уведомления
            if (!_meetupSettings.IsEnabled)
            {
                return; 
            }

            // Отправка предварительного уведомления
            var preNotificationTime = _meetupSettings.NotificationTime - _meetupSettings.PreNotificationTime;
            if (!_meetupSettings.PreNotificationSent && currentTime.TimeOfDay >= preNotificationTime &&
                currentTime.TimeOfDay < _meetupSettings.NotificationTime)
            {
                var preNotificationMessage = "Напоминание: митап начнется через " + _meetupSettings.PreNotificationTime.ToString(@"hh\:mm") + "!";
                _botClient.SendTextMessageAsync(_meetupSettings.AdminChatId, preNotificationMessage).Wait();
                _meetupSettings.PreNotificationSent = true; // Устанавливаем флаг, что уведомление отправлено
            }

            // Отправка уведомления о начале митапа
            if (currentTime.TimeOfDay >= _meetupSettings.NotificationTime &&
                currentTime.TimeOfDay < _meetupSettings.NotificationTime + TimeSpan.FromMinutes(1)) // Условие, чтобы уведомление отправлялось один раз
            {
                var notificationMessage = "Митап начинается прямо сейчас!";
                _botClient.SendTextMessageAsync(_meetupSettings.AdminChatId, notificationMessage).Wait();
            }
        }

        static async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.Type != UpdateType.Message || update.Message.Type != MessageType.Text)
                return;

            var message = update.Message;

            if (message != null && message.Type == MessageType.Text)
            {
                if (message.Text.StartsWith("/setadmin"))
                {
                    _adminChatId = message.Chat.Id;
                    await botClient.SendTextMessageAsync(message.Chat, "Идентификатор администратора чата успешно установлен!");
                }
                if (message.Chat.Id == _adminChatId)
                {     
                    if (message.Text.StartsWith("/startmeetup"))
                    {
                        // Команда для начала митапа
                        var meetupId = DateTime.Now.Ticks; // Уникальный ID митапа
                        _meetups[meetupId] = new MeetupDetails
                        {
                            StartTime = DateTime.Now,
                            Participants = new List<long>()
                        };
                        await botClient.SendTextMessageAsync(message.Chat, "Митап начался!");
                    }
                    else if (message.Text.StartsWith("/endmeetup"))
                    {
                        {
                            // Команда для завершения митапа
                            var meetupId = _meetups.Keys.LastOrDefault(); // Получаем последний митап
                            if (meetupId != 0)
                            {
                                var meetup = _meetups[meetupId];
                                meetup.EndTime = DateTime.Now;
                                await botClient.SendTextMessageAsync(message.Chat, "Митап завершен!");

                                // Запись в БД
                                using (var connection = new SqlConnection(_connectionString))
                                {
                                    var command = new SqlCommand("INSERT INTO Meetups (MeetupId, StartTime, EndTime) VALUES (@MeetupId, @StartTime, @EndTime)", connection);
                                    command.Parameters.AddWithValue("@MeetupId", meetupId);
                                    command.Parameters.AddWithValue("@StartTime", meetup.StartTime);
                                    command.Parameters.AddWithValue("@EndTime", meetup.EndTime);
                                    connection.Open();
                                    command.ExecuteNonQuery();
                                }
                                _meetups.Remove(meetupId);
                            }
                        }
                    }
            
                    else if (message.Text.StartsWith("/enable_notifications"))
                    {
                        _notificationsEnabled = true;
                        await botClient.SendTextMessageAsync(message.Chat, "Уведомления включены!");
                    }
                    else if (message.Text.StartsWith("/disable_notifications"))
                    {
                         _notificationsEnabled = false;
                         await botClient.SendTextMessageAsync(message.Chat, "Уведомления отключены!");
                    }
                    else if (message.Text.StartsWith("/set_pre_notification_time"))
                    {
                        if (TimeSpan.TryParse(message.Text.Split(' ').Last(), out var newTime))
                        {
                            _preNotificationTime = newTime;
                            _meetupSettings.PreNotificationTime = newTime; // Обновление настроек
                            SaveSettings(); // Сохранение изменений в appsettings.json
                            await botClient.SendTextMessageAsync(message.Chat, $"Время предварительного уведомления установлено на {newTime}");
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(message.Chat, "Неверный формат времени. Используйте HH:mm:ss.");
                        }
                    }

                    else if (message.Text.StartsWith("/help"))
                    {
                            var helpMessage = "Доступные команды:\n" +
                            "/setadmin - Установить администратора чата.\n" +
                            "/startmeetup - Начать митап.\n" +
                            "/endmeetup - Завершить митап.\n" +
                            "/enable_notifications - Включить уведомления.\n" +
                            "/disable_notifications - Отключить уведомления.\n" +
                            "/cancelmeetup - Отменяет запланированный митап.\n" +
                            "/set_pre_notification_time HH:mm:ss. - Установить время предварительного уведомления.\n" +
                            "/reschedulemeetup HH:mm:ss. - Переносит митап на указанное время.\n" +                    
                            "/setnotificationtime HH:mm:ss. - Устанавливает новое время уведомлений.";
                        await botClient.SendTextMessageAsync(message.Chat, helpMessage);
                    } 


                    else if (message.Text.StartsWith("/reschedulemeetup"))
                    {
                        // Команда для переноса митапа
                        var parts = message.Text.Split(' ');
                        if (parts.Length > 1 && DateTime.TryParse(parts[1], out var newStartTime))
                        {
                            _meetupSettings.NotificationTime = newStartTime.TimeOfDay;
                            SaveSettings(); // Сохранение изменений в appsettings.json
                            await botClient.SendTextMessageAsync(message.Chat, $"Митап перенесен на {newStartTime:HH:mm}");
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(message.Chat, "Неверный формат времени. Используйте HH:mm.");
                        }
                    }


                    else if (message.Text.StartsWith("/cancelmeetup"))
                    {
                        // Команда для отмены митапа
                        _meetupSettings.NotificationTime = TimeSpan.Zero; 
                        SaveSettings(); 
                        await botClient.SendTextMessageAsync(message.Chat, "Митап отменен.");
                    }


                    else if (message.Text.StartsWith("/setnotificationtime"))
                    {
                        // Команда для изменения времени уведомлений
                        var parts = message.Text.Split(' ');
                        if (parts.Length > 1 && TimeSpan.TryParse(parts[1], out var newNotificationTime))
                        {
                            _meetupSettings.NotificationTime = newNotificationTime;
                            SaveSettings(); 
                            await botClient.SendTextMessageAsync(message.Chat, $"Время уведомлений изменено на {newNotificationTime}");
                        }
                        else
                        {
                            await botClient.SendTextMessageAsync(message.Chat, "Неверный формат времени. Используйте HH:mm:ss.");
                        }
                    }
                }
            }

            // Добавление других команд и их обработчиков
        }

        static Task HandleErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            Console.WriteLine(exception.ToString());
            return Task.CompletedTask;
        }

        static void SaveSettings()
        {
            // Загрузка текущей конфигурации
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .Build();

            // Получение секции MeetupNotifications и обновление только PreNotificationTime
            var section = configuration.GetSection("MeetupNotifications");
            section["AdminChatId"] = _adminChatId.ToString();
            section["PreNotificationTime"] = _meetupSettings.PreNotificationTime.ToString(@"hh\:mm\:ss");            
            section["NotificationTime"] = _meetupSettings.NotificationTime.ToString(@"hh\:mm\:ss");
            section["IsEnabled"] = _meetupSettings.IsEnabled.ToString();

            // Создание объекта для сохранения изменений
            var updatedSettings = new
            {
                TelegramBotToken = section["TelegramBotToken"],
                MeetupNotifications = new
                {
                    AdminChatId = section["AdminChatId"],
                    NotificationTime = section["NotificationTime"],
                    PreNotificationTime = section["PreNotificationTime"],
                    IsEnabled = section["IsEnabled"]
                },
                ConnectionStrings = new
                {
                    DefaultConnection = configuration.GetConnectionString("DefaultConnection")
                }
            };

            // Сохранение изменений в файл
            System.IO.File.WriteAllText("appsettings.json", JsonConvert.SerializeObject(updatedSettings, Formatting.Indented));
        }
    }

    class MeetupDetails
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public List<long> Participants { get; set; }
    }

    //  класс для хранения настроек митапов
    public class MeetupNotificationSettings
    {
        public long AdminChatId { get; set; }
        public TimeSpan NotificationTime { get; set; }
        public TimeSpan PreNotificationTime { get; set; }
        public bool IsEnabled { get; set; }
        public bool PreNotificationSent { get; set; }
    }
}

class SimpleHandler : ICommandHandler
{
    private readonly Action<long> _action;

    public SimpleHandler(Action<long> action)
    {
        _action = action;
    }

    public Task<int> InvokeAsync(InvocationContext context)
    {
        var chatId = context.ParseResult.GetValueForArgument<long>(new Argument<long>("chat-id"));
        _action(chatId);
        return Task.FromResult(0);
    }

    public int Invoke(InvocationContext context)
    {
        
        var chatId = context.ParseResult.GetValueForArgument<long>(new Argument<long>("chat-id"));
        _action(chatId);
        return 0;
    }
}
