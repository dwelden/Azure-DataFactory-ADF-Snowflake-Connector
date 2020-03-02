
# Azure Data Factory ADF Snowflake Connector V3 (Dynamic Credentials & SQL with no timeouts ) #

  

***03/01/2020 - Added multi SQL statement support within a single call. Simply add additional SQL queries in the "query": parameter sepearated by ; character***

  


Finally easy to use Azure Data Factory (ADF) Snowflake connector that has no timeout issues for long running queries. This connector allows you to execute a single SQL statement at a time from ADF to execute in Snowflake warehouse. (*SQL can be any valid SQL including COPY, Select, Stored Procs, DML & etc.*)

It took a while make it work but I was finally able to create a function that could run in ADF without any timeout limitations using Azure's cheaper shared function plans vs. dedicated ones.

Before I explain how it works, let me shared Azure function limitations for long runnning processes that I encountered and the reasoning behind the logic behind this function.

When using Azure Funtions in ADF, there are two seperate time-outs that you have to deal with.
 1. HTTP Rest call timeout (230 secs) that can NOT be changed. When ADF makes a rest call to any https end-point, it has to receive some reply in less than 230 secs or that ADF step will fail. This means you can't build a function that receives a SQL, executes it & waits for query to finish than replies when it is done with results as many queries can easily take longer than 230 secs to complete & will be a failed task in ADF.
 
 2. For long running tasks like an ETL SQL execution, Azure than introduces Durable functions. They are designed to respond to a REST call right away with a URL link that the caller can make another call to see that status of the initial request. After that durable function can run the long running task. In the mean time, Client will repeately call the Status URL to see if the initial request it done. When the long running task is completed successfully in the back ground , function will respond with http-200 OK message along with JSON body that can include details about the result. This basically eliminate HTTP 230 secs time-out issue as it responds to each call right away however durable functions also have a time limit (10 to 30 mins) that they can execute tasks in the background if you are using one of Azure shared plans. Time limit is gone only if you go with dedicated plan which costs more. This option got me up to 10 mins of execution plan before it timed out.

Because of these limitations, my first version of ADF Snowflake connector execute any query as passthrough which meant that it sent it but did not wait for an answer and always reported success. This mean, ADF developers had to monitor Snowflake if any of the pipeline steps failed or not.  After my inital version, the feedback I got from Snowflake users was pretty much the same things.

 - They wanted a solution where ADF step should to fail if the Query failed so they could tell the pipeline failed from ADF w/o monitoring snowflake query history.
 - They needed the use query outcome (suchs rows effected) to use it in their pipeline decisions down stream.
 - They prefered not to pay for dedicated Function App instances.

Solution was a regular app that mimiced the output of a Durable function. Major change was where the durable function was actually executing & waiting for query to be finished to report back the result, I had to build something that didnt waited around for long runnning queries to finish. Below is how it was done to satisfy all 3 requests from Ver1 users.

 1. ADF makes a rest call to Snowflake_Function & submits a JSON payload. (JSON includes Snowflake connection parameters + the SQL statement to execute)
 2. Snowflake_Function add a unique comment tag to the end of the SQL query for tracking purposes & executes it as a ExecuteNonQuery which means it doesnt wait for a result & moves on.
 3. It then immediately queries the Snowflake_Query_History view for that unique tag to find the QUERYID of that original query. (it will repeat this every 5 secs for a min until it can locate it). It runs this as a regular query since it runs quickly & returns a single row.
 4. it appends the QUERYID + Connection parameters in to string then encrypts it using a custom PASSCODE value you define as part of the setup. It immedeately replies to ADF with a status URL that includes this encrypted text as a URL parameter.
 5.  Snowflake_Function's output is a URL that allows ADF WEB request to monitor the status of the original query. URL includes encrypted info about the QUERYID & the snowflake connection parameters. 
 6. When WEB step call the STATUS URL, function recognizes this a STATUS check instead of new SQL QUERY. It decypts the URL parameters to extract the QueryID + Connection Info. Then queries the Snowflake query_history view for that QueryID using the connection info that it receives. It checks the Query_Status columns and responds back based on different statuses such as COMPLETE, RUNNING, FAILED & etc. Response also has specific HTTP Status codes to let an ADF WEB step to retry if the status is not complete.
 7. if the WEB step gets a response indicating status is RUNNING, it re-tries the same URL in X seconds configured in its properties. If Status is COMPLETE, it receives a JSON payload showing the QueryExecution results from the History View such as Status, RecordsEffected & etc. When this happens, it stops retrying and passes the JSON as its output.
 8. ADF users can then use these results to drive their ADF pipeline logic downstream to make new call.

Using this technique that mimics a durable function, this regular function never waits for long running queries to execute. It just passes them to snowflake and moves on w/o getting a response and gets their QueryID to report back to the caller . Subsequents calls for Status checks are executed quickly against the query_history view using the Query_ID and take few seconds at most. This way each REST call whether it is to execute a ETL query or for a Status check, is responded within seconds without any Azure timeout limitaions and final outcome is a query status of SUCCESS or FAIL along with JSON payload of query_execution results if it is a PASS.

All you have to do is to add 2 ADF steps for each ETL call to snowflake. First workflow step is the AzureFunction call with the Query you want to execute. Second step is a Web step to wait until the original query is executed by repeatedly calling the Status URL which is the output of Step1.

 Here is how you install & configure the Azure data factory and the function:

ist item

  

Typical usage would be to place this at the end of a data pipeline and issue a copy command from Snowflake once Data Factory generates data files in an Azure blob storage.

  

<img src="https://github.com/NickAkincilar/Azure-DataFactory-ADF-Snowflake-Connector/blob/master/images/ADF_Function_Chart.png?raw=true" alt="drawing" width="900"/>

  

Function is essentially a rest endpoint which accepts a POST request which needs to contain the following JSON payload in the body of the request. JSON payload is a combination of SF connection parameters plus the SQL command that needs to be executed.

  

Execution Logic is follows:

1. Create an ADF data pipe line to create necessary data files on a Azure blob stage

2. ADF creates data file(s) in the Azure blob storage

3. Upon successfull execution of step 2, ADF makes a call to Snowflake_ADF Azure function with a JSON data (Configure JSON body with connection + proper copy command to execute )

4. Snowflake_ADF Function makes a .Net connection to Snowflake using the connection parameteres from JSON payload and executes the embedded SQL command.

5. Snowflake executes the COPY from @STAGE command to ingest the newly created files by ADF.

  

<hr>

  

Connection attributes & the SQL command can be inserted in to JSON two different ways.

  

- **Using dynamic ADF variables.** Credentials & other attributes can be dynamically fetched from **Azure Key Vault** Secret keys where they are securely stored. (_Preferred_)

  

{

"account":"your_SF_account",

"host":"",

"userid":"@{variables('v_user')}",

"pw":"@{variables('v_pw')}",

"database":"db_name",

"schema":"PUBLIC",

"role":"Your_ROLE",

"warehouse":"Warehouse_Name",

"query":"Copy into SomeTable from @YourStage;"

}

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Credentials_From_KeyVault.png" alt="drawing" width="500"/>

  

You can use web requests to fetch Azure Vault Secrets & set it as variable values to be passed to this custom function as user credentials.

<hr>

  

- **As Static Values** Credentials can also be stored as part of the JSON input of the Azure function properties within the ADF Pipeline

  

{

"account":"your_SF_account",

"host":"",

"userid":"YourUserName",

"pw":"YourPassword",

"database":"db_name",

"schema":"PUBLIC",

"role":"Your_ROLE",

"warehouse":"Warehouse_Name",

"query":"Copy into SomeTable from @YourStage;"

}

  
  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Credentials_Static.png" alt="drawing" width="500"/>

  

# Setup (Part 1) - Create Snowflake Azure Function

  

  

1. Create a new Azure Function

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00001.png" alt="drawing" width="300"/>

  

  

2. Set the BASICS as following

  

1. **Resource Group** = Create a new one or use an existing one

  

2. **Function App Name** = Provide a unique name for your function

  

3. **Runtime Stack** : .NET Core

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00002.png" alt="drawing" width="500"/>

  

  

3. Set the HOSTING properties as:

  

- Storage Account = Create New or use existing

  

- Operating System = Windows

  

- Plan Type = You Choice

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00003.png" alt="drawing" width="500"/>

  

4. Following the following steps &amp; click CREATE to finish the initial phase

  

5. It will take few minutes to deploy it.

  
  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00004.png" alt="drawing" width="500"/>

  

6. Once finished, click on GO TO RESOURCE

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00005.png" alt="drawing" width="500"/>

  

  

7. Click on NEW FUNCTION

  
  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00006.png" alt="drawing" width="500"/>

  

8. Use IN-PORTAL option

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00007.png" alt="drawing" width="500"/>

  

9. Use WEBHOOK + API

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00008.png" alt="drawing" width="500"/>

  

  

10. It will automatically create a function called HTTPTrigger1 which you need to delete

  

- Click on FUNCTIONS on the left

  

- Click on Trash can Icon to delete for HTTPTrigger1

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00009.png" alt="drawing" width="500"/>

  

11. Click on NEW FUNCTION to add your own

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00010.png" alt="drawing" width="500"/>

  

  

12. Choose HTTP Trigger

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00011.png" alt="drawing" width="500"/>

  

13. Give it a unique name for the call

  

1. Authorization level determines who can call this function?

  

- **Anonymous** = REST endpoint is open to public (still need to provide proper credentials for snowflake)

  

- **Function** = This requires sender to provide a unique API security key to make a connection o this REST end point

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00012.png" alt="drawing" width="400"/>

  

14. Override the code the in RUN.CSX.

  

15. Copy &amp; Paste code from RUN.CSX file on [from this project](https://github.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/blob/master/ADF_RestCallSnowflake/run.csx) &amp; SAVE.

  

16. Copy necessary supporting files &amp; driver

  

1. Click on the FUNCTION APP name

  

2. Click PLATFORM FEATURES tab

  

3. Click Advanced tools(Kudu)

  

- Choose - Debug Console

  

- Pick - POWERSHELL

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00013.png" alt="drawing" width="500"/>

  

  

17. Navigate to site following path = \wwwroot\Your\_Function\_Name\

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00014.png" alt="drawing" width="500"/>

  

18. [Download](https://github.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/archive/master.zip) the repository from this project &amp; unzip.

  

19. Drag &amp; drop the BIN folder in to the files area under your function folder

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00015.png" alt="drawing" width="800"/>

  

20. If you choose, Function Auth Level as. FUNCTION then you need to get the API call key to call this function

  

21. Go back to Function area

  

1. Click on **Function name**

  

2. Click on **Manage**

  

3. Click on **COPY** for the default function key &amp;  **store it in a text editor.**

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00016.png" alt="drawing" width="500"/>

  

  
  

# Setup (Part 2) - USE THE FUNCTION IN AZURE DATA FACTORY PIPELINE

  

1. Add Azure Function on to canvas

  

2. Give it a name

- **Check Secure Input & Output** to hide connection info from being logged. <br/><br/>

  

  
  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00017.png" alt="drawing" width="400"/>

  

  

3. Switch to SETTINGS

  

- **+ NEW** to add a new AZURE FUNCTION LINKED SERVICE

  

- Set All Required Properties

  

- Function Key = **Key value from step 21** (_if FUNCTION mode is used for security_)

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00018.png" alt="drawing" width="500"/>

  

- Function Name = **Function name from Step #13**

  

- Method = **POST**

  

  

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00019.png" alt="drawing" width="500"/>

  

- Set BODY to following JSON format with Snowflake connection & SQL command attributes

  
  

{

"account":"your_SF_account",

"host":"",

"userid":"YourUserName",

"pw":"YourPassword",

"database":"db_name",

"schema":"PUBLIC",

"role":"Your_ROLE",

"warehouse":"Warehouse_Name",

"query":"Copy into SomeTable from @YourStage;"

}

  

  

4. **DEBUG** on the PIPELINE to test.

  

  

If the data pipeline was able to successfully create output files, it will trigger the Azure Function and that would connect to Snowflake and Execute the SQL command using the attributes of the JSON post data.

<img src="https://raw.githubusercontent.com/NickAkincilar/Snowflake-Azure-DataFactory-Connector/master/images/Screenshot00020.png" alt="drawing" width="800"/>