#r "Newtonsoft.Json"
#r "Snowflake.Data.dll"
#r "System.Configuration.ConfigurationManager.dll"
#r "Newtonsoft.Json"
#r "Microsoft.Azure.WebJobs.Extensions.dll"
#r "Microsoft.Azure.WebJobs.Extensions.Http.dll"
#r "log4net.dll"

using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Snowflake.Data.Client;
using System.Data;

        public static  async Task<IActionResult> Run( [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,ILogger log)
        {
  
            string MyRecCount = "";
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            if (requestBody.Length == 0)
            {
                MyRecCount = null;
                goto skipthis;
            }
            string SFconnectionString = "";
            string SQL_Command ="";
            
            dynamic data = JsonConvert.DeserializeObject(requestBody);

            string Account_UID = data?.userid;
            string Account_PW = data?.pw;
            string Account_ID = data?.account;
            string Account_role = data?.role;
            string sf_host = data?.host;
            if (sf_host.Length == 0)
            {
                sf_host = ".";
            }
            else
            {
                sf_host = "." + sf_host + ".";
            }

            string Account_db = data?.database;
            string Account_Schema = data?.schema;
            string Account_WH = data?.warehouse;



            string Account_Host = Account_ID + sf_host + "snowflakecomputing.com";
            SQL_Command = data?.query;
       
            SFconnectionString = "account={0};user={1};password={2};host={3};db={4};warehouse={5};role={6};schema={7};";
            SFconnectionString = String.Format(SFconnectionString, Account_ID, Account_UID, Account_PW, Account_Host, Account_db, Account_WH, Account_role, Account_Schema);
        

            Task<string> task;

            string[] SQLList = SQL_Command.Split(";", StringSplitOptions.RemoveEmptyEntries);

            foreach (string SQLQuery in SQLList)
            {
                task = RunQuery(SFconnectionString, SQLQuery);
            }

            MyRecCount = "Command Done";


                     skipthis:
                    string SampleJSON;
                            SampleJSON = @"{
                ""account"": """",
                ""host"": """",
                ""userid"": """",
                ""pw"": """",
                ""database"": """",
                ""schema"": """",
                ""role"": """",
                ""warehouse"": """",
                ""query"":""""
                }";


            string SampleOut;
            SampleOut = "{\"result\": \"" + MyRecCount + "\"}";

            return MyRecCount != null
            ? (ActionResult)new OkObjectResult(SampleOut)
            : new BadRequestObjectResult("Please post followiing data in the request body: \n" + SampleJSON);

        }


        public static async Task<string> RunQuery(string SFC, string TSQL)
        {
            string MyResult = "";
            SnowflakeDbConnection myConnection = new SnowflakeDbConnection();
            myConnection.ConnectionString = SFC;
            try
            {
                SnowflakeDbCommand myCommandmaster = new SnowflakeDbCommand(myConnection);
                if (myConnection.IsOpen() == false)
                {
               
                    await myConnection.OpenAsync();
                }

                myCommandmaster = new SnowflakeDbCommand(myConnection);
                myCommandmaster.CommandText = TSQL;
                SnowflakeDbDataAdapter MasterSQLDataAdapter;
                MasterSQLDataAdapter = new SnowflakeDbDataAdapter(myCommandmaster);
                try
                {
 
                    await myCommandmaster.ExecuteNonQueryAsync();
                 
                    MyResult = "Command Done";
                    return MyResult;
                }
                catch (Exception ex)
                {
                    MyResult = ex.Message.ToString();
                    return MyResult;
                }

            }
            catch (Exception ex)
            {
                MyResult = ex.Message.ToString();
                return MyResult;
            }



        }
