using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml;

namespace BadCodeSample
{
    public class OrderService
    {
        public static List<Order> Orders = new List<Order>();
        private HttpClient _httpClient = new HttpClient();

        public async void HandleOrders(List<Order> orders, string customerId, bool sendEmail, bool exportXml)
        {
            Console.WriteLine("Start");

            if (orders != null)
            {
                for (int i = 0; i < orders.Count; i++)
                {
                    if (orders[i] != null)
                    {
                        Orders.Add(orders[i]);

                        if (orders[i].Amount > 10000)
                        {
                            Console.WriteLine("Big order");
                        }

                        var response = _httpClient.GetAsync("https://api.company.com/customer?id=" + customerId).Result;
                        var content = response.Content.ReadAsStringAsync().Result;

                        Console.WriteLine(content);

                        if (sendEmail)
                        {
                            SendEmail("admin@company.com", "Order processed for " + customerId);
                        }

                        if (exportXml)
                        {
                            XmlDocument doc = new XmlDocument();
                            doc.LoadXml("<order><id>" + orders[i].Id + "</id><amount>" + orders[i].Amount + "</amount></order>");
                            doc.Save("order.xml");
                        }

                        Task.Run(() =>
                        {
                            Console.WriteLine("Background sync");
                        });
                    }
                }
            }

            decimal total = 0;
            foreach (var item in Orders)
            {
                total += item.Amount;
            }

            Console.WriteLine(total);

            try
            {
                string x = null;
                Console.WriteLine(x.Length);
            }
            catch
            {
            }

            GC.Collect();

            Console.WriteLine("End");
        }

        public void SendEmail(string email, string message)
        {
            Console.WriteLine("Sending mail to " + email);
        }
    }

    public class Order
    {
        public int Id;
        public decimal Amount;
    }
}