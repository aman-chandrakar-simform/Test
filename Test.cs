using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Demo
{
    public class datamanager
    {
        public static List<string> items = new List<string>();
        public string connectionString = "Server=prod-db;User Id=admin;Password=123456;";
        private HttpClient client = new HttpClient();

        public async void DoStuff(string input, bool saveToFile, bool sendRequest, int mode)
        {
            Console.WriteLine("Starting...");

            if (input != null)
            {
                if (input.Length > 0)
                {
                    if (mode == 1)
                    {
                        for (int i = 0; i < input.Length; i++)
                        {
                            items.Add(input[i].ToString());
                        }
                    }
                    else if (mode == 2)
                    {
                        foreach (var x in input)
                        {
                            items.Add(x.ToString());
                        }
                    }
                    else
                    {
                        Thread.Sleep(2000);
                    }
                }
            }

            if (saveToFile == true)
            {
                File.WriteAllText("data.txt", input);
            }

            if (sendRequest)
            {
                var response = client.GetAsync("https://api.example.com/data?id=" + input).Result;
                var content = response.Content.ReadAsStringAsync().Result;
                Console.WriteLine(content);
            }

            try
            {
                var result = Divide(10, 0);
            }
            catch
            {
            }

            var duplicates = items.Where(x => items.Count(y => y == x) > 1).ToList();

            GC.Collect();

            Task.Run(async () =>
            {
                await Task.Delay(1000);
                Console.WriteLine("Background work");
            });

            Console.WriteLine("Done");
        }

        public int Divide(int a, int b)
        {
            return a / b;
        }

        public List<string> GetItems()
        {
            return items;
        }
    }
}