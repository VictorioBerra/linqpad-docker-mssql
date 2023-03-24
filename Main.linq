<Query Kind="Program">
  <NuGetReference>Dapper</NuGetReference>
  <NuGetReference>Docker.DotNet</NuGetReference>
  <NuGetReference>Microsoft.Data.SqlClient</NuGetReference>
  <NuGetReference>Z.Dapper.Plus</NuGetReference>
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
		var startingTimestamp = Stopwatch.GetTimestamp();
		
		var imageName = "mcr.microsoft.com/mssql/server";
		var imageTag = "latest";
		var containerName = "linqpad-sql1";
		var sqlServerPassword = "<YourStrong@Passw0rd>";
		var deleteContainerIfExists = false;
		var deleteContainerWhenDone = false;
		var pullLatestImage = true;
		var intervalBetweenHealthyQuery = TimeSpan.FromSeconds(2);
		
		DockerClient client = new DockerClientConfiguration().CreateClient();

		var createSQLServerContainerResponse = default(CreateSQLServerContainerResponse);
		try
		{
			if (pullLatestImage)
			{
				await PullLatestMicrosoftSQL(client, imageName, imageTag);
			}
			createSQLServerContainerResponse = await CreateSQLServerContainerAsync(
				client,
				imageName,
				sqlServerPassword,
				containerName, 
				deleteContainerIfExists);
			connectionString = createSQLServerContainerResponse.MicrosoftSQLServerConnectionString;
			
			// Skip if using existing container
			if (createSQLServerContainerResponse.ExistingContainer is null)
			{
				"Waiting for health checks.".Dump();
				await WaitUntilContainerHealthy(client, createSQLServerContainerResponse.ContainerID, intervalBetweenHealthyQuery);	
			}
			
			if (createSQLServerContainerResponse.ExistingContainer is not null)
			{
				"Creating database...".Dump();
				CreateDatabase();
			}
			else
			{
				"Existing Container Found, skipping database creation.".Dump();
			}
			
			connectionString += "Database=TestDB;";

			if (createSQLServerContainerResponse.ExistingContainer is not null)
			{
				"Creating database tables...".Dump();
				InitializeDatabase();
			}
			else
			{
				"Existing Container Found, skipping database tables creation.".Dump();
			}

			// ---
			// Your code here
			// ---
			"Running user code".Dump();
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
			if (deleteContainerWhenDone)
			{
				"Deleting container...".Dump();
				await DeleteContainerAsync(client, createSQLServerContainerResponse.ContainerID);
			}
			else
			{
				"Skipping container delete due to option deleteContainerWhenDone.".Dump();
			}
			
			var elapsedMilliseconds = Stopwatch.GetElapsedTime(startingTimestamp).TotalMilliseconds;			
			$"app;dur={elapsedMilliseconds}.0".Dump();
		}
	}
	
	public static async Task DeleteContainerAsync(DockerClient client, string containerId)
	{
		await client.Containers.StopContainerAsync(containerId, new ContainerStopParameters());
		await client.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters());
	}
	
	public static async Task PullLatestMicrosoftSQL(DockerClient client, string imageName, string tag)
	{
		await client.Images
			.CreateImageAsync(
				new ImagesCreateParameters()
				{
					FromImage = imageName,
					Tag = tag,
				},
				new AuthConfig(),
				new Progress<JSONMessage>(x => $"{x.Status} - {x.Progress}".Dump()));
	}
	
	public static async Task WaitUntilContainerHealthy(
		DockerClient client,
		string containerId,
		TimeSpan intervalBetweenHealthyQuery)
	{
		var healthy = false;
		while (!healthy)
		{
			var i = await client.Containers.InspectContainerAsync(containerId);
			
			var healthStatus = i?.State?.Health?.Status;
			
			if (string.IsNullOrEmpty(healthStatus))
			{
				throw new ArgumentException("healthStatus Should never be null. Something is wrong with the health check configuration.", nameof(healthStatus));
			}
			else if (healthStatus == "starting")
			{
				"Still starting...".Dump();
				healthy = false;
			}
			else if (healthStatus == "unhealthy")
			{
				i?.State?.Health.Dump();
				throw new InvalidProgramException("Container status went unhealthy. Abandoning.");
			}
			else if (healthStatus == "healthy")
			{
				"Container is healthy.".Dump();
				healthy = true;
			}
			
			Thread.Sleep((int)intervalBetweenHealthyQuery.TotalMilliseconds);
		}
	}

	public static async Task<CreateSQLServerContainerResponse> CreateSQLServerContainerAsync(
		DockerClient client,
		string imageName,
		string sqlServerPassword,
		string containerName,
		bool deleteIfExists)
	{
		var connectionString = $"Server=localhost;User Id=SA;Password={sqlServerPassword};Trust Server Certificate=true;";

		var containerListResponse = await client.Containers.ListContainersAsync(new ContainersListParameters());
		containerListResponse.Select(x => new
		{
			ID = x.ID,
			FirstName = x.Names.First(),
			Image = x.Image,
		}).Dump("List of containers on your system");
		
		var existingContainer = containerListResponse.FirstOrDefault(x => x.Names.Contains("/" + containerName));
		if (existingContainer is not null)
		{
			$"Found container with name {containerName} - ID: {existingContainer.ID}".Dump();
			if (deleteIfExists)
			{
				$"Deleting because deleteIfExists = true".Dump();
				await DeleteContainerAsync(client, existingContainer.ID);
			}
			else
			{
				// Using existing
				return new CreateSQLServerContainerResponse(existingContainer.ID, connectionString)
				{
					ExistingContainer = existingContainer,
				};
			}
		}

		$"Creating {imageName} container with name {containerName}.".Dump();
		var createContainerResponse = await client.Containers.CreateContainerAsync(new CreateContainerParameters()
		{
			Image = imageName,
			Name = containerName,
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
			Healthcheck = new HealthConfig
			{
				Interval = TimeSpan.FromSeconds(1),
				Timeout = TimeSpan.FromSeconds(20),
				Retries = 3,
				StartPeriod = (long)TimeSpan.FromSeconds(5).TotalNanoseconds,
				Test = new List<string>
				{
					"CMD",
					$"/opt/mssql-tools/bin/sqlcmd",
					"-S",
					"localhost",
					"-U",
					"SA",
					"-P",
					sqlServerPassword,
					"-Q",
					"Select 1",
				}
			},
		});

		$"Starting {imageName} container with name {containerName}.".Dump();
		var started = await client.Containers.StartContainerAsync(createContainerResponse.ID, new ContainerStartParameters());

		if (!started)
		{
			throw new InvalidProgramException("client.Containers.StartContainerAsync returned false.");
		}

		return new CreateSQLServerContainerResponse(createContainerResponse.ID, connectionString)
		{
			CreatedContainer = createContainerResponse,
		};
	}

	public class CreateSQLServerContainerResponse
	{
		public CreateSQLServerContainerResponse(string containerID, string microsoftSQLServerConnectionString)
		{
			this.ContainerID = containerID;
			this.MicrosoftSQLServerConnectionString = microsoftSQLServerConnectionString;
		}
		
		/// <summary>Either the existing, or created container ID.</summary>
		public string ContainerID  { get; set; }
		
		public CreateContainerResponse CreatedContainer  { get; set; }
		
		public ContainerListResponse ExistingContainer  { get; set; }
		
		public string MicrosoftSQLServerConnectionString  { get; set; }
	}

	public static void CreateDatabase()
	{
		var connection = new SqlConnection(connectionString);

		connection.Execute(@"
CREATE DATABASE TestDB
");
	}

	public static void InitializeDatabase()
	{
		var connection = new SqlConnection(connectionString);

		connection.Execute(@"
	
CREATE TABLE [dbo].[MyTable](
	[Id] [INT] NOT NULL,
	[SomeString] [nvarchar](30) NOT NULL,
	[SomeDate] [datetimeoffset](7) NULL,
 CONSTRAINT [MyTablePK] PRIMARY KEY CLUSTERED 
(
	[Id] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF, IGNORE_DUP_KEY = OFF, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON) ON [PRIMARY]
) ON [PRIMARY];

INSERT INTO MyTable (Id, SomeString, SomeDate) VALUES (1, 'Hello World', '07/31/1989');
INSERT INTO MyTable (Id, SomeString, SomeDate) VALUES (2, 'Hello Mars', '07/31/2023');
INSERT INTO MyTable (Id, SomeString, SomeDate) VALUES (3, 'Hello WorlVenusd', '07/31/2099');
");
	}
}