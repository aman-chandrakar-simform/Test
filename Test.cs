using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace ReviewDemo
{
    public class EmployeeProcessor
    {
        public static Dictionary<int, string> Employees = new Dictionary<int, string>();

        public async void ProcessEmployees(List<string> employeeNames)
        {
            Console.WriteLine("Processing employees");

            SqlConnection conn = new SqlConnection("Server=prod-db;Database=HR;User Id=sa;Password=Password123;");
            conn.Open();

            foreach (var name in employeeNames)
            {
                if (name != null)
                {
                    if (name.Length > 0)
                    {
                        Employees.Add(Employees.Count + 1, name);

                        File.AppendAllText("employees.log", name + Environment.NewLine);

                        WebClient client = new WebClient();
                        var result = client.DownloadString("https://api.company.com/check?name=" + name);

                        Console.WriteLine(result);

                        await Task.Delay(100);
                    }
                }
            }

            string query = "SELECT * FROM Employees WHERE Name = '" + employeeNames[0] + "'";
            SqlCommand cmd = new SqlCommand(query, conn);
            var reader = cmd.ExecuteReader();

            while (reader.Read())
            {
                Console.WriteLine(reader["Name"]);
            }

            conn.Close();

            try
            {
                int x = 10;
                int y = 0;
                Console.WriteLine(x / y);
            }
            catch
            {
            }

            GC.Collect();
        }

        public Dictionary<int, string> GetEmployees()
        {
            return Employees;
        }
    }
}