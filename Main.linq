<Query Kind="Program">
  <NuGetReference>Dapper</NuGetReference>
  <NuGetReference>Docker.DotNet</NuGetReference>
  <NuGetReference>Microsoft.Data.SqlClient</NuGetReference>
  <Namespace>Dapper</Namespace>
  <Namespace>Docker.DotNet</Namespace>
  <Namespace>Docker.DotNet.Models</Namespace>
  <Namespace>Microsoft.Data.SqlClient</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
  <Namespace>Z.Dapper.Plus</Namespace>
</Query>

public class Program
{
	public static string connectionString = string.Empty;
	
	public static async Task Main()
	{
		// For a more rapid developer loop, run this once as null, then again with the connection string to use an existing container
		string useExisting = "Server=localhost;User Id=SA;Database=TestDB;Password=<YourStrong@Passw0rd>;Trust Server Certificate=true;";
		connectionString = useExisting;
		
		DockerClient client = new DockerClientConfiguration().CreateClient();

		var createSQLServerContainerResponse = default(CreateSQLServerContainerResponse);
		try
		{
			if (useExisting == null)
			{
				createSQLServerContainerResponse = await CreateSQLServerContainerAsync(client); // Heads up, this calls `CREATE DATABASE TestDB;`
				connectionString = createSQLServerContainerResponse.MicrosoftSQLServerConnectionString;
			}
			
			CreateDatabase();
			
			// ---
			// Your code here
			// ---
			var connection = new SqlConnection(connectionString);
			var testDatabaseTable = connection.Query("SELECT TOP 10 * FROM MyTable;");
			testDatabaseTable.Dump();
		}
		catch(Exception ex)
		{
			throw;
		}
		finally
		{
			if (useExisting == null)
			{
				await DeleteContainerAsync(client, createSQLServerContainerResponse.ContainerID);
			}
		}
	}
	
	public static async Task DeleteContainerAsync(DockerClient client, string containerId)
	{
		await client.Containers.StopContainerAsync(containerId, new ContainerStopParameters());
		await client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters());
	}
	
	public static async Task<CreateSQLServerContainerResponse> CreateSQLServerContainerAsync(DockerClient client)
	{
		var sqlServerPassword = "<YourStrong@Passw0rd>";
		var connectionString = $"Server=localhost;User Id=SA;Database=TestDB;Password={sqlServerPassword};Trust Server Certificate=true; Connect Timeout = 5;";

		var containerListResponse = await client.Containers.ListContainersAsync(new ContainersListParameters());
		containerListResponse.Select(x => new
		{
			ID = x.ID,
			FirstName = x.Names.First(),
			Image = x.Image,
		}).Dump();

		var sqlServerContainer = await client.Containers.CreateContainerAsync(new CreateContainerParameters()
		{
			Image = "mcr.microsoft.com/mssql/server:2022-latest",
			Hostname = "sql1",
			ExposedPorts = new Dictionary<string, EmptyStruct>
			{
				{ "1433", default }
			},
			HostConfig = new HostConfig
			{
				PortBindings = new Dictionary<string, IList<PortBinding>>
				{
					{
						"1433",
						new List<PortBinding>
						{
							new PortBinding
							{
								HostIP = "0.0.0.0",
								HostPort = "1433",
							},
						}
					},
				},
			},
			Env = new List<string>
			{
				"ACCEPT_EULA=Y",
				"MSSQL_SA_PASSWORD=" + sqlServerPassword,
			},
		});

		await client.Containers.StartContainerAsync(sqlServerContainer.ID, new ContainerStartParameters());

		int timeoutSeconds = 60.Dump("Health Check Timeout"); ;
		int retryWaitSeconds = 2.Dump("Health Check Retry Wait"); ;
		DateTime timeout = DateTime.Now.AddSeconds(timeoutSeconds);

		"Waiting for SQL Server to be healthy...".Dump();
		while (DateTime.Now < timeout)
		{
			try
			{
				var connection = new SqlConnection($"Server=localhost;User Id=SA;Password={sqlServerPassword};Trust Server Certificate=true; Connect Timeout = 5;");
				connection.Execute("SELECT 'READY'");
				"SQL Server healthy!".Dump();
				connection.Execute("CREATE DATABASE TestDB;");
				break;
			}
			catch (SqlException sqlEx)
			{
				if (sqlEx.ErrorCode == -2146232060)
				{
					// Commented out because chatty. Uncomment for more debug information.
					// $"SqlException, connection was forcibly closed. SQL Server is probably still setting up.".Dump();
				}
				else
				{
					sqlEx.Dump(collapseTo: 0);
				}

				// Commented out because chatty. Uncomment for more debug information.
				// $"Retrying in {retryWaitSeconds} seconds.".Dump();
				Thread.Sleep(retryWaitSeconds * 1000);
			}
		}

		return new CreateSQLServerContainerResponse(sqlServerContainer.ID, connectionString);
	}

	public class CreateSQLServerContainerResponse
	{
		public CreateSQLServerContainerResponse(string containerID, string microsoftSQLServerConnectionString)
		{
			this.ContainerID = containerID;
			this.MicrosoftSQLServerConnectionString = microsoftSQLServerConnectionString;
		}
		
		public string ContainerID  { get; set; }
		
		public string MicrosoftSQLServerConnectionString  { get; set; }
	}

	public static void CreateDatabase()
	{
		var connection = new SqlConnection(connectionString);

		connection.Execute(@"
	
CREATE TABLE [dbo].[MyTable](
	[Id] [INT] NOT NULL,
	[SomeString] [nvarchar](30) NOT NULL,
	[SomeDate] [datetimeoffset](7) NULL,
 CONSTRAINT [MyTable] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY];		
");
	}
}