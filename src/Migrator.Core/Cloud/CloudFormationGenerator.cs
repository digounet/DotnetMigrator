using System.Text;
using System.Text.RegularExpressions;
using Migrator.Core.Analysis;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>
/// CloudFormation counterpart of <see cref="InfrastructureGenerator"/>: a set of independent stacks under infra/cloudformation/
/// (network → data → storage → compute → lambda), linked by exports, plus parameter files, deploy scripts, a README and the
/// GitHub Actions workflow. With <c>--target framework</c> the compute stack is EC2 Windows + CodeDeploy (code unchanged);
/// with the .NET 10 target it is ECS Fargate + Lambda, mirroring the Terraform module. Nothing here contains credentials.
/// </summary>
public static partial class CloudFormationGenerator
{
    public const string Dir = "infra/cloudformation";
    public const string CodeDeployDir = "infra/codedeploy";

    private sealed record Deployable(ProjectResult Result, ApplicationProfile Profile, string Slug, string Id, string Logical);

    public static Dictionary<string, string> Generate(SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> profiles)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var arch = result.Architecture;
        if (arch == null) return files;
        var components = arch.Components.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var app = Slug(result.SolutionName);
        var framework = result.Options.KeepsFramework;
        var deployables = profiles
            .Where(p => p.Result.Hosting is { Primary: not (AwsHosting.NotDeployable or AwsHosting.Desktop) } && p.Result.OutputProjectPath != null)
            .Select(p => new Deployable(p.Result, p.Profile, Slug(p.Result.Project.Name), Id(p.Result.Project.Name), Logical(p.Result.Project.Name)))
            .ToList();
        if (deployables.Count == 0) return files;
        var webs = deployables.Where(d => d.Result.Project.Kind == ProjectKind.Web).ToList();
        var lambdas = framework ? [] : deployables.Where(d => d.Result.Hosting!.Primary == AwsHosting.Lambda).ToList();
        var workers = framework ? [] : deployables.Where(d => d.Result.Hosting!.Primary == AwsHosting.EcsFargateWorker).ToList();
        var fileAutomations = lambdas.Where(l => FileDriven(l.Profile)).ToList();
        var hasDb = result.Databases.Count > 0;
        var hasS3 = components.Contains("s3") || fileAutomations.Count > 0;
        var hasFsx = framework && components.Contains("fsx");

        files[$"{Dir}/00-network.yaml"] = Network(framework, components);
        if (hasDb) files[$"{Dir}/10-data.yaml"] = Data(result);
        if (hasS3 || hasFsx || workers.Count > 0) files[$"{Dir}/20-storage.yaml"] = Storage(hasS3, hasFsx, fileAutomations, workers);
        files[$"{Dir}/30-compute.yaml"] = framework ? ComputeEc2(result, deployables, webs, components, hasDb, hasS3) : ComputeEcs(result, deployables, webs, components, hasDb, hasS3);
        if (lambdas.Count > 0) files[$"{Dir}/40-lambda.yaml"] = Lambda(result, lambdas, hasDb, hasS3);

        var stacks = files.Keys.Where(k => k.EndsWith(".yaml", StringComparison.Ordinal)).Select(k => Path.GetFileNameWithoutExtension(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        foreach (var stack in stacks) files[$"{Dir}/parameters/{stack}.json"] = Parameters(stack, app, result, deployables, webs, lambdas, hasDb);
        files[$"{Dir}/deploy.sh"] = DeployBash(app, stacks);
        files[$"{Dir}/deploy.ps1"] = DeployPowerShell(app, stacks);
        if (framework)
            foreach (var d in deployables)
                foreach (var (path, content) in CodeDeployBundle(app, d))
                    files[$"{CodeDeployDir}/{d.Slug}/{path}"] = content;
        var notGenerated = profiles.Where(p => p.Result.Hosting is { Primary: not (AwsHosting.NotDeployable or AwsHosting.Desktop) } && p.Result.OutputProjectPath == null).Select(p => p.Result).ToList();
        files["infra/README.md"] = Readme(result, deployables, lambdas, notGenerated, stacks, components, framework);
        files[".github/workflows/deploy.yml"] = framework ? WorkflowWindows(app, result, deployables) : InfrastructureGenerator.Workflow(app, deployables.Where(d => d.Result.Hosting!.Primary != AwsHosting.Lambda).Select(d => (d.Result, d.Profile)).ToList(), lambdas.Select(d => (d.Result, d.Profile)).ToList(), result);
        return files;
    }

    private static bool FileDriven(ApplicationProfile p) => p.HasAny(Signal.FileWatcher, Signal.Ftp, Signal.MailboxReading) || (p.HasAny(Signal.FileSystemWrites, Signal.UncPaths, Signal.WindowsPaths) && p.Has(Signal.SpreadsheetFiles)) || !p.HasAny(Signal.Msmq, Signal.RabbitMq, Signal.MessageBusFramework, Signal.Kafka, Signal.AzureServiceBus);

    // ------------------------------------------------------------------ 00-network

    private static string Network(bool framework, HashSet<string> components)
    {
        var endpoints = new List<string> { "ssm", "ssmmessages", "ec2messages", "secretsmanager", "logs" };
        if (!framework) endpoints.AddRange(["ecr.api", "ecr.dkr"]);
        var sb = new StringBuilder();
        sb.AppendLine("""
            AWSTemplateFormatVersion: "2010-09-09"
            Description: "Rede da aplicação (VPC, subnets públicas/privadas, NAT único e VPC endpoints). Gerado pelo Migrator."

            Parameters:
              AppName:
                Type: String
                Description: Nome da aplicação (prefixo dos recursos e dos exports).
              Environment:
                Type: String
                Default: prod
                AllowedValues: [dev, hml, prod]
              VpcCidr:
                Type: String
                Default: 10.40.0.0/16

            Resources:
              Vpc:
                Type: AWS::EC2::VPC
                Properties:
                  CidrBlock: !Ref VpcCidr
                  EnableDnsHostnames: true
                  EnableDnsSupport: true
                  Tags:
                    - Key: Name
                      Value: !Sub "${AppName}-${Environment}"
              InternetGateway:
                Type: AWS::EC2::InternetGateway
              VpcGatewayAttachment:
                Type: AWS::EC2::VPCGatewayAttachment
                Properties:
                  VpcId: !Ref Vpc
                  InternetGatewayId: !Ref InternetGateway
              PublicSubnetA:
                Type: AWS::EC2::Subnet
                Properties:
                  VpcId: !Ref Vpc
                  AvailabilityZone: !Select [0, !GetAZs ""]
                  CidrBlock: !Select [8, !Cidr [!Ref VpcCidr, 16, 12]]
                  MapPublicIpOnLaunch: true
                  Tags: [{ Key: Name, Value: !Sub "${AppName}-${Environment}-public-a" }]
              PublicSubnetB:
                Type: AWS::EC2::Subnet
                Properties:
                  VpcId: !Ref Vpc
                  AvailabilityZone: !Select [1, !GetAZs ""]
                  CidrBlock: !Select [9, !Cidr [!Ref VpcCidr, 16, 12]]
                  MapPublicIpOnLaunch: true
                  Tags: [{ Key: Name, Value: !Sub "${AppName}-${Environment}-public-b" }]
              PrivateSubnetA:
                Type: AWS::EC2::Subnet
                Properties:
                  VpcId: !Ref Vpc
                  AvailabilityZone: !Select [0, !GetAZs ""]
                  CidrBlock: !Select [0, !Cidr [!Ref VpcCidr, 16, 12]]
                  Tags: [{ Key: Name, Value: !Sub "${AppName}-${Environment}-private-a" }]
              PrivateSubnetB:
                Type: AWS::EC2::Subnet
                Properties:
                  VpcId: !Ref Vpc
                  AvailabilityZone: !Select [1, !GetAZs ""]
                  CidrBlock: !Select [1, !Cidr [!Ref VpcCidr, 16, 12]]
                  Tags: [{ Key: Name, Value: !Sub "${AppName}-${Environment}-private-b" }]
              PublicRouteTable:
                Type: AWS::EC2::RouteTable
                Properties:
                  VpcId: !Ref Vpc
              PublicRoute:
                Type: AWS::EC2::Route
                DependsOn: VpcGatewayAttachment
                Properties:
                  RouteTableId: !Ref PublicRouteTable
                  DestinationCidrBlock: 0.0.0.0/0
                  GatewayId: !Ref InternetGateway
              PublicSubnetARoute:
                Type: AWS::EC2::SubnetRouteTableAssociation
                Properties: { SubnetId: !Ref PublicSubnetA, RouteTableId: !Ref PublicRouteTable }
              PublicSubnetBRoute:
                Type: AWS::EC2::SubnetRouteTableAssociation
                Properties: { SubnetId: !Ref PublicSubnetB, RouteTableId: !Ref PublicRouteTable }
              # Um NAT para começar; para alta disponibilidade crie um segundo na zona B (custo fixo por NAT).
              NatEip:
                Type: AWS::EC2::EIP
                DependsOn: VpcGatewayAttachment
                Properties:
                  Domain: vpc
              NatGateway:
                Type: AWS::EC2::NatGateway
                Properties:
                  AllocationId: !GetAtt NatEip.AllocationId
                  SubnetId: !Ref PublicSubnetA
              PrivateRouteTable:
                Type: AWS::EC2::RouteTable
                Properties:
                  VpcId: !Ref Vpc
              PrivateRoute:
                Type: AWS::EC2::Route
                Properties:
                  RouteTableId: !Ref PrivateRouteTable
                  DestinationCidrBlock: 0.0.0.0/0
                  NatGatewayId: !Ref NatGateway
              PrivateSubnetARoute:
                Type: AWS::EC2::SubnetRouteTableAssociation
                Properties: { SubnetId: !Ref PrivateSubnetA, RouteTableId: !Ref PrivateRouteTable }
              PrivateSubnetBRoute:
                Type: AWS::EC2::SubnetRouteTableAssociation
                Properties: { SubnetId: !Ref PrivateSubnetB, RouteTableId: !Ref PrivateRouteTable }
              # Endpoints evitam que o tráfego para serviços AWS passe pelo NAT Gateway (custo por GB).
              S3Endpoint:
                Type: AWS::EC2::VPCEndpoint
                Properties:
                  VpcId: !Ref Vpc
                  ServiceName: !Sub "com.amazonaws.${AWS::Region}.s3"
                  VpcEndpointType: Gateway
                  RouteTableIds: [!Ref PrivateRouteTable]
              EndpointSecurityGroup:
                Type: AWS::EC2::SecurityGroup
                Properties:
                  GroupDescription: Interface endpoints
                  VpcId: !Ref Vpc
                  SecurityGroupIngress:
                    - IpProtocol: tcp
                      FromPort: 443
                      ToPort: 443
                      CidrIp: !Ref VpcCidr
            """);
        foreach (var service in endpoints)
        {
            sb.AppendLine($"  {Logical(service)}Endpoint:");
            sb.AppendLine("    Type: AWS::EC2::VPCEndpoint");
            sb.AppendLine("    Properties:");
            sb.AppendLine("      VpcId: !Ref Vpc");
            sb.AppendLine($"      ServiceName: !Sub \"com.amazonaws.${{AWS::Region}}.{service}\"");
            sb.AppendLine("      VpcEndpointType: Interface");
            sb.AppendLine("      PrivateDnsEnabled: true");
            sb.AppendLine("      SubnetIds: [!Ref PrivateSubnetA, !Ref PrivateSubnetB]");
            sb.AppendLine("      SecurityGroupIds: [!Ref EndpointSecurityGroup]");
        }
        if (components.Contains("vpn"))
            sb.AppendLine("""
                  # Conectividade híbrida: a aplicação depende de hosts on-premises. Descomente e preencha com o gateway do datacenter.
                  # CustomerGateway:
                  #   Type: AWS::EC2::CustomerGateway
                  #   Properties: { BgpAsn: 65000, IpAddress: 203.0.113.10, Type: ipsec.1 }
                  # VpnGateway:
                  #   Type: AWS::EC2::VPNGateway
                  #   Properties: { Type: ipsec.1 }
                  # VpnAttachment:
                  #   Type: AWS::EC2::VPCGatewayAttachment
                  #   Properties: { VpcId: !Ref Vpc, VpnGatewayId: !Ref VpnGateway }
                  # VpnConnection:
                  #   Type: AWS::EC2::VPNConnection
                  #   Properties: { Type: ipsec.1, CustomerGatewayId: !Ref CustomerGateway, VpnGatewayId: !Ref VpnGateway, StaticRoutesOnly: true }
                  # VpnRoute:
                  #   Type: AWS::EC2::VPNConnectionRoute
                  #   Properties: { DestinationCidrBlock: 10.0.0.0/8, VpnConnectionId: !Ref VpnConnection }
                  # DNS interno: Route 53 Resolver outbound endpoint + regra de encaminhamento para o domínio corporativo.
                """);
        sb.AppendLine("""

            Outputs:
              VpcId:
                Value: !Ref Vpc
                Export: { Name: !Sub "${AppName}-${Environment}-VpcId" }
              VpcCidr:
                Value: !Ref VpcCidr
                Export: { Name: !Sub "${AppName}-${Environment}-VpcCidr" }
              PublicSubnets:
                Value: !Join [",", [!Ref PublicSubnetA, !Ref PublicSubnetB]]
                Export: { Name: !Sub "${AppName}-${Environment}-PublicSubnets" }
              PrivateSubnets:
                Value: !Join [",", [!Ref PrivateSubnetA, !Ref PrivateSubnetB]]
                Export: { Name: !Sub "${AppName}-${Environment}-PrivateSubnets" }
              PrivateSubnetA:
                Value: !Ref PrivateSubnetA
                Export: { Name: !Sub "${AppName}-${Environment}-PrivateSubnetA" }
            """);
        return Yaml(sb);
    }

    // ------------------------------------------------------------------ 10-data

    internal static string DefaultEngine(SolutionResult result) => result.Databases.Select(d => d.Provider).FirstOrDefault() switch
    {
        "Oracle" => "oracle-se2", "MySQL" => "mysql", "PostgreSQL" => "postgres", _ => "sqlserver-ex"
    };

    private static int DefaultPort(string engine) => engine.StartsWith("sqlserver", StringComparison.Ordinal) ? 1433 : engine.StartsWith("oracle", StringComparison.Ordinal) ? 1521 : engine == "mysql" ? 3306 : 5432;

    private static string Data(SolutionResult result)
    {
        var engine = DefaultEngine(result);
        var names = string.Join(", ", result.Databases.Select(d => d.Database ?? d.Name).Distinct().Take(5));
        var sqlServer = engine.StartsWith("sqlserver", StringComparison.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine($$"""
            AWSTemplateFormatVersion: "2010-09-09"
            Description: "Banco(s) detectado(s): {{names}}. Migre os dados com restore nativo (.bak via S3) ou AWS DMS. Gerado pelo Migrator."

            Parameters:
              AppName:
                Type: String
              Environment:
                Type: String
                Default: prod
                AllowedValues: [dev, hml, prod]
              DbEngine:
                Type: String
                Default: {{engine}}
                Description: "Engine do RDS{{(sqlServer ? " (sqlserver-ex é gratuito em licença até 10 GB por base; sqlserver-web, sqlserver-se, sqlserver-ee)" : "")}}."
              DbPort:
                Type: Number
                Default: {{DefaultPort(engine)}}
              DbInstanceClass:
                Type: String
                Default: {{(sqlServer ? "db.t3.small" : "db.t4g.medium")}}
              DbAllocatedStorage:
                Type: Number
                Default: 50
              DbUsername:
                Type: String
                Default: appadmin
              LicenseModel:
                Type: String
                Default: "{{(sqlServer || engine == "oracle-se2" ? "license-included" : "")}}"
                Description: "license-included para SQL Server/Oracle SE2; vazio para os demais engines."

            Conditions:
              IsProd: !Equals [!Ref Environment, prod]
              HasLicenseModel: !Not [!Equals [!Ref LicenseModel, ""]]

            Resources:
              DbSubnetGroup:
                Type: AWS::RDS::DBSubnetGroup
                Properties:
                  DBSubnetGroupDescription: !Sub "${AppName}-${Environment}"
                  SubnetIds: !Split [",", !ImportValue { "Fn::Sub": "${AppName}-${Environment}-PrivateSubnets" }]
              # Sem ingress aqui: a stack de compute libera a porta a partir do security group da aplicação.
              DbSecurityGroup:
                Type: AWS::EC2::SecurityGroup
                Properties:
                  GroupDescription: RDS
                  VpcId: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-VpcId" }
              Database:
                Type: AWS::RDS::DBInstance
                DeletionPolicy: Snapshot
                UpdateReplacePolicy: Snapshot
                Properties:
                  DBInstanceIdentifier: !Sub "${AppName}-${Environment}"
                  Engine: !Ref DbEngine
                  DBInstanceClass: !Ref DbInstanceClass
                  AllocatedStorage: !Ref DbAllocatedStorage
                  StorageType: gp3
                  StorageEncrypted: true
                  Port: !Ref DbPort
                  DBSubnetGroupName: !Ref DbSubnetGroup
                  VPCSecurityGroups: [!Ref DbSecurityGroup]
                  MasterUsername: !Ref DbUsername
                  ManageMasterUserPassword: true # senha gerenciada e rotacionada pelo Secrets Manager
                  MultiAZ: !If [IsProd, true, false]
                  BackupRetentionPeriod: 7
                  DeletionProtection: !If [IsProd, true, false]
                  PubliclyAccessible: false
                  LicenseModel: !If [HasLicenseModel, !Ref LicenseModel, !Ref "AWS::NoValue"]

            Outputs:
              DbEndpoint:
                Value: !GetAtt Database.Endpoint.Address
                Export: { Name: !Sub "${AppName}-${Environment}-DbEndpoint" }
              DbPort:
                Value: !Ref DbPort
                Export: { Name: !Sub "${AppName}-${Environment}-DbPort" }
              DbSecurityGroupId:
                Value: !Ref DbSecurityGroup
                Export: { Name: !Sub "${AppName}-${Environment}-DbSecurityGroupId" }
              DbMasterSecretArn:
                Description: Segredo gerenciado pelo RDS com a senha do usuário master.
                Value: !GetAtt Database.MasterUserSecret.SecretArn
                Export: { Name: !Sub "${AppName}-${Environment}-DbMasterSecretArn" }
            """);
        return Yaml(sb);
    }

    // ------------------------------------------------------------------ 20-storage

    private static string Storage(bool hasS3, bool hasFsx, List<Deployable> fileAutomations, List<Deployable> workers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            AWSTemplateFormatVersion: "2010-09-09"
            Description: "Armazenamento (bucket S3 privado, filas SQS e compartilhamento FSx quando aplicável). Gerado pelo Migrator."

            Parameters:
              AppName:
                Type: String
              Environment:
                Type: String
                Default: prod
                AllowedValues: [dev, hml, prod]
            """);
        if (hasFsx)
            sb.AppendLine("""
                  ActiveDirectoryId:
                    Type: String
                    Default: ""
                    Description: "ID do diretório (AWS Managed Microsoft AD, d-xxxx) exigido pelo FSx for Windows; vazio = não criar o FSx."
                  FsxStorageCapacity:
                    Type: Number
                    Default: 32
                    Description: GB (mínimo 32).
                  FsxThroughputCapacity:
                    Type: Number
                    Default: 32
                    Description: MB/s.
                """);
        if (hasFsx)
        {
            sb.AppendLine();
            sb.AppendLine("Conditions:");
            sb.AppendLine("  HasActiveDirectory: !Not [!Equals [!Ref ActiveDirectoryId, \"\"]]");
        }
        sb.AppendLine();
        sb.AppendLine("Resources:");
        if (hasS3)
        {
            if (fileAutomations.Count > 0)
                sb.AppendLine("""
                      # Cada arquivo novo no bucket vira uma mensagem: retry e DLQ nativos; a Lambda consome a fila (stack 40-lambda).
                      FilesEventsDlq:
                        Type: AWS::SQS::Queue
                        Properties:
                          QueueName: !Sub "${AppName}-${Environment}-files-events-dlq"
                      FilesEventsQueue:
                        Type: AWS::SQS::Queue
                        Properties:
                          QueueName: !Sub "${AppName}-${Environment}-files-events"
                          VisibilityTimeout: 900
                          RedrivePolicy:
                            deadLetterTargetArn: !GetAtt FilesEventsDlq.Arn
                            maxReceiveCount: 5
                      FilesEventsQueuePolicy:
                        Type: AWS::SQS::QueuePolicy
                        Properties:
                          Queues: [!Ref FilesEventsQueue]
                          PolicyDocument:
                            Version: "2012-10-17"
                            Statement:
                              - Effect: Allow
                                Principal: { Service: s3.amazonaws.com }
                                Action: sqs:SendMessage
                                Resource: !GetAtt FilesEventsQueue.Arn
                                Condition:
                                  ArnLike: { "aws:SourceArn": !Sub "arn:aws:s3:::${AppName}-${Environment}-${AWS::AccountId}-files" }
                                  StringEquals: { "aws:SourceAccount": !Ref "AWS::AccountId" }
                    """);
            sb.AppendLine("  FilesBucket:");
            sb.AppendLine("    Type: AWS::S3::Bucket");
            if (fileAutomations.Count > 0) sb.AppendLine("    DependsOn: FilesEventsQueuePolicy");
            sb.AppendLine("""
                    Properties:
                      BucketName: !Sub "${AppName}-${Environment}-${AWS::AccountId}-files"
                      PublicAccessBlockConfiguration:
                        BlockPublicAcls: true
                        BlockPublicPolicy: true
                        IgnorePublicAcls: true
                        RestrictPublicBuckets: true
                      VersioningConfiguration:
                        Status: Enabled
                      BucketEncryption:
                        ServerSideEncryptionConfiguration:
                          - ServerSideEncryptionByDefault: { SSEAlgorithm: AES256 }
                      LifecycleConfiguration:
                        Rules:
                          - Id: processados-para-glacier
                            Status: Enabled
                            Prefix: processados/
                            Transitions:
                              - TransitionInDays: 90
                                StorageClass: GLACIER_IR
                """);
            if (fileAutomations.Count > 0)
            {
                sb.AppendLine("      NotificationConfiguration:");
                sb.AppendLine("        QueueConfigurations:");
                foreach (var l in fileAutomations)
                {
                    sb.AppendLine("          - Event: s3:ObjectCreated:*");
                    sb.AppendLine("            Queue: !GetAtt FilesEventsQueue.Arn");
                    sb.AppendLine("            Filter:");
                    sb.AppendLine("              S3Key:");
                    sb.AppendLine($"                Rules: [{{ Name: prefix, Value: {l.Slug}/entrada/ }}]");
                }
            }
        }
        foreach (var w in workers)
            sb.AppendLine($$"""
                  {{w.Logical}}Dlq:
                    Type: AWS::SQS::Queue
                    Properties:
                      QueueName: !Sub "${AppName}-${Environment}-{{w.Slug}}-dlq"
                  {{w.Logical}}Queue:
                    Type: AWS::SQS::Queue
                    Properties:
                      QueueName: !Sub "${AppName}-${Environment}-{{w.Slug}}"
                      VisibilityTimeout: 300
                      RedrivePolicy:
                        deadLetterTargetArn: !GetAtt {{w.Logical}}Dlq.Arn
                        maxReceiveCount: 5
                """);
        if (hasFsx)
            sb.AppendLine("""
                  # Pastas de rede (\\servidor\pasta) com o mesmo caminho SMB, sem mudar o código. Copie os dados com robocopy ou AWS DataSync.
                  FsxSecurityGroup:
                    Type: AWS::EC2::SecurityGroup
                    Condition: HasActiveDirectory
                    Properties:
                      GroupDescription: FSx for Windows (SMB)
                      VpcId: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-VpcId" }
                      SecurityGroupIngress:
                        - { IpProtocol: tcp, FromPort: 445, ToPort: 445, CidrIp: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-VpcCidr" } }
                        - { IpProtocol: tcp, FromPort: 135, ToPort: 135, CidrIp: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-VpcCidr" } }
                        - { IpProtocol: tcp, FromPort: 49152, ToPort: 65535, CidrIp: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-VpcCidr" } }
                  FileShare:
                    Type: AWS::FSx::FileSystem
                    Condition: HasActiveDirectory
                    Properties:
                      FileSystemType: WINDOWS
                      StorageType: SSD
                      StorageCapacity: !Ref FsxStorageCapacity
                      SubnetIds: [!ImportValue { "Fn::Sub": "${AppName}-${Environment}-PrivateSubnetA" }]
                      SecurityGroupIds: [!Ref FsxSecurityGroup]
                      WindowsConfiguration:
                        ActiveDirectoryId: !Ref ActiveDirectoryId
                        ThroughputCapacity: !Ref FsxThroughputCapacity
                        DeploymentType: SINGLE_AZ_2 # MULTI_AZ_1 em produção (dobra o custo; exige PreferredSubnetId e duas subnets)
                        AutomaticBackupRetentionDays: 7
                      Tags: [{ Key: Name, Value: !Sub "${AppName}-${Environment}-files" }]
                """);
        sb.AppendLine();
        sb.AppendLine("Outputs:");
        if (hasS3)
            sb.AppendLine("""
                  FilesBucketName:
                    Value: !Ref FilesBucket
                    Export: { Name: !Sub "${AppName}-${Environment}-FilesBucketName" }
                  FilesBucketArn:
                    Value: !GetAtt FilesBucket.Arn
                    Export: { Name: !Sub "${AppName}-${Environment}-FilesBucketArn" }
                """);
        if (fileAutomations.Count > 0)
            sb.AppendLine("""
                  FilesEventsQueueArn:
                    Value: !GetAtt FilesEventsQueue.Arn
                    Export: { Name: !Sub "${AppName}-${Environment}-FilesEventsQueueArn" }
                """);
        foreach (var w in workers)
            sb.AppendLine($$"""
                  {{w.Logical}}QueueUrl:
                    Value: !Ref {{w.Logical}}Queue
                    Export: { Name: !Sub "${AppName}-${Environment}-{{w.Logical}}QueueUrl" }
                  {{w.Logical}}QueueArn:
                    Value: !GetAtt {{w.Logical}}Queue.Arn
                    Export: { Name: !Sub "${AppName}-${Environment}-{{w.Logical}}QueueArn" }
                """);
        if (hasFsx)
            sb.AppendLine("""
                  FsxDnsName:
                    Condition: HasActiveDirectory
                    Description: Monte como \\<dns>\share nas instâncias (o nome do compartilhamento padrão é "share").
                    Value: !GetAtt FileShare.DNSName
                    Export: { Name: !Sub "${AppName}-${Environment}-FsxDnsName" }
                """);
        return Yaml(sb);
    }

    // ------------------------------------------------------------------ parameters, scripts, README

    private static string Parameters(string stack, string app, SolutionResult result, List<Deployable> deployables, List<Deployable> webs, List<Deployable> lambdas, bool hasDb)
    {
        var list = new List<(string Key, string Value)> { ("AppName", app), ("Environment", "hml") };
        switch (stack)
        {
            case "00-network": list.Add(("VpcCidr", "10.40.0.0/16")); break;
            case "10-data": list.Add(("DbEngine", DefaultEngine(result))); list.Add(("DbInstanceClass", DefaultEngine(result).StartsWith("sqlserver", StringComparison.Ordinal) ? "db.t3.small" : "db.t4g.medium")); break;
            case "20-storage": if (result.Options.KeepsFramework) list.Add(("ActiveDirectoryId", "")); break;
            case "30-compute":
                list.Add(("CertificateArn", ""));
                list.Add(("AlertEmail", ""));
                if (result.Options.KeepsFramework) { list.Add(("InstanceType", "t3.medium")); list.Add(("HealthCheckPath", "/")); list.Add(("TimeZone", "E. South America Standard Time")); }
                else list.Add(("ImageTag", "latest"));
                foreach (var w in webs) list.Add(($"Host{w.Logical}", $"{w.Slug}-hml.empresa.com.br"));
                if (!result.Options.KeepsFramework)
                    foreach (var s in deployables.Where(d => d.Result.Hosting!.Primary == AwsHosting.EcsScheduledTask)) list.Add(($"Schedule{s.Logical}", "rate(15 minutes)"));
                break;
            case "40-lambda":
                list.Add(("PackageBucket", $"{app}-hml-ARTIFACTS"));
                foreach (var l in lambdas) list.Add(($"PackageKey{l.Logical}", $"lambda/{l.Slug}.zip"));
                if (lambdas.Any(l => l.Profile.Has(Signal.MailboxReading))) { list.Add(("MailboxPollSchedule", "rate(5 minutes)")); list.Add(("EnableSesInbound", "false")); list.Add(("InboundRecipients", "")); }
                break;
        }
        var sb = new StringBuilder();
        sb.AppendLine("[");
        for (var i = 0; i < list.Count; i++)
            sb.AppendLine($"  {{ \"ParameterKey\": \"{list[i].Key}\", \"ParameterValue\": \"{list[i].Value}\" }}{(i < list.Count - 1 ? "," : "")}");
        sb.AppendLine("]");
        return Yaml(sb);
    }

    private static string DeployBash(string app, List<string> stacks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("# Cria/atualiza as stacks na ordem certa. Uso: ./deploy.sh [ambiente] [região]  (padrão: hml sa-east-1)");
        sb.AppendLine("# Edite parameters/<stack>.json antes (nada ali é segredo). Gerado pelo Migrator.");
        sb.AppendLine("set -euo pipefail");
        sb.AppendLine("cd \"$(dirname \"$0\")\"");
        sb.AppendLine($"APP=\"{app}\"");
        sb.AppendLine("ENV=\"${1:-hml}\"");
        sb.AppendLine("REGION=\"${2:-sa-east-1}\"");
        sb.AppendLine();
        sb.AppendLine("deploy() {");
        sb.AppendLine("  local stack=\"$1\"");
        sb.AppendLine("  echo \"==> $APP-$ENV-${stack#*-}\"");
        sb.AppendLine("  aws cloudformation deploy --region \"$REGION\" \\");
        sb.AppendLine("    --template-file \"$stack.yaml\" \\");
        sb.AppendLine("    --stack-name \"$APP-$ENV-${stack#*-}\" \\");
        sb.AppendLine("    --parameter-overrides \"file://parameters/$stack.json\" \\");
        sb.AppendLine("    --capabilities CAPABILITY_NAMED_IAM --no-fail-on-empty-changeset");
        sb.AppendLine("}");
        sb.AppendLine();
        foreach (var stack in stacks) sb.AppendLine($"deploy {stack}");
        sb.AppendLine();
        sb.AppendLine("aws cloudformation describe-stacks --region \"$REGION\" --stack-name \"$APP-$ENV-compute\" --query \"Stacks[0].Outputs\" --output table");
        return Yaml(sb);
    }

    private static string DeployPowerShell(string app, List<string> stacks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Cria/atualiza as stacks na ordem certa. Uso: .\\deploy.ps1 [-Environment hml] [-Region sa-east-1]");
        sb.AppendLine("# Edite parameters\\<stack>.json antes (nada ali é segredo). Gerado pelo Migrator.");
        sb.AppendLine("param([string]$Environment = \"hml\", [string]$Region = \"sa-east-1\")");
        sb.AppendLine("$ErrorActionPreference = \"Stop\"");
        sb.AppendLine("Set-Location $PSScriptRoot");
        sb.AppendLine($"$App = \"{app}\"");
        sb.AppendLine();
        sb.AppendLine("function Deploy-Stack([string]$Stack) {");
        sb.AppendLine("  $name = \"$App-$Environment-\" + ($Stack -replace '^\\d+-', '')");
        sb.AppendLine("  Write-Host \"==> $name\"");
        sb.AppendLine("  aws cloudformation deploy --region $Region --template-file \"$Stack.yaml\" --stack-name $name --parameter-overrides \"file://parameters/$Stack.json\" --capabilities CAPABILITY_NAMED_IAM --no-fail-on-empty-changeset");
        sb.AppendLine("  if ($LASTEXITCODE -ne 0) { throw \"Falha na stack $name\" }");
        sb.AppendLine("}");
        sb.AppendLine();
        foreach (var stack in stacks) sb.AppendLine($"Deploy-Stack \"{stack}\"");
        sb.AppendLine();
        sb.AppendLine("aws cloudformation describe-stacks --region $Region --stack-name \"$App-$Environment-compute\" --query \"Stacks[0].Outputs\" --output table");
        return Yaml(sb);
    }

    private static string Readme(SolutionResult result, List<Deployable> deployables, List<Deployable> lambdas, List<ProjectResult> notGenerated, List<string> stacks, HashSet<string> components, bool framework)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Infraestrutura de {result.SolutionName} na AWS (CloudFormation)");
        sb.AppendLine();
        sb.AppendLine(framework
            ? "Gerado pelo Migrator com `--target framework`: o código continua em .NET Framework 4.8.1 e roda em EC2 Windows (lift-and-shift). Veja `_migration-report/migration-report.html`, seção \"Arquitetura alvo\", para a justificativa e para a hospedagem que cada projeto teria após a migração para .NET 10. É um ponto de partida revisável, não um ambiente de produção pronto."
            : "Gerado pelo Migrator a partir da arquitetura proposta (veja `_migration-report/migration-report.html`, seção \"Arquitetura alvo\"). É um ponto de partida revisável, não um ambiente de produção pronto: leia os comentários, ajuste tamanhos, domínios e políticas antes do deploy.");
        sb.AppendLine();
        sb.AppendLine("## Stacks (na ordem)");
        sb.AppendLine();
        foreach (var stack in stacks)
            sb.AppendLine($"- `{stack}.yaml` → stack `<app>-<ambiente>-{stack[3..]}`: " + stack switch
            {
                "00-network" => "VPC, subnets públicas/privadas, NAT único, VPC endpoints" + (components.Contains("vpn") ? " (esqueleto comentado da VPN)" : ""),
                "10-data" => $"RDS ({DefaultEngine(result)} por padrão) com senha master gerenciada pelo Secrets Manager; a porta é liberada pela stack de compute",
                "20-storage" => "bucket S3 privado/versionado/criptografado" + (framework && components.Contains("fsx") ? ", FSx for Windows (exige `ActiveDirectoryId`)" : "") + (lambdas.Count > 0 ? ", fila de eventos do bucket" : ""),
                "30-compute" => framework
                    ? "IAM, security groups, ALB, launch templates e Auto Scaling groups por projeto (EC2 Windows Server 2022, user data instala IIS/.NET 4.8.1/agentes), CodeDeploy, bucket de artefatos, alarmes"
                    : "cluster ECS, ECR, IAM, ALB, task definitions e serviços web, tarefas agendadas (EventBridge Scheduler), workers com SQS, alarmes",
                "40-lambda" => "funções Lambda (.NET), DLQ, gatilhos (fila de eventos do S3, agendamento para caixa postal, SES recebimento opcional)",
                _ => ""
            });
        sb.AppendLine();
        sb.AppendLine("## O que é criado por projeto");
        sb.AppendLine();
        foreach (var d in deployables) sb.AppendLine($"- **{d.Result.Project.Name}** → {d.Result.Hosting!.Primary.Display()}");
        foreach (var missing in notGenerated) sb.AppendLine($"- **{missing.Project.Name}** → {missing.Hosting!.Primary.Display()}: não gerado ({(missing.Project.IsVisualBasic ? "projeto VB.NET não convertido; converta e rode o Migrator de novo" : "projeto sem saída migrada")}).");
        sb.AppendLine();
        sb.AppendLine("## Ordem de execução");
        sb.AppendLine();
        var n = 1;
        sb.AppendLine($"{n++}. **Parâmetros**: edite `parameters/<stack>.json` (ambiente, hosts, engine do banco{(framework ? ", tipo de instância, caminho do health check" : ", tag da imagem")}). Nada ali é segredo.");
        sb.AppendLine($"{n++}. **Segredos**: " + (framework
            ? "crie no Secrets Manager um segredo `<app>/<projeto>/config` por projeto, JSON chave→valor (`\"ConnectionStrings:DefaultConnection\": \"...\"`, `\"AppSettings:Chave\": \"...\"`); o `after-install.ps1` do CodeDeploy grava os valores no web.config/app.config da instância."
            : "rode `_secrets/<projeto>/create-secrets.sh` para cada projeto (as task definitions referenciam os segredos pelo nome)."));
        sb.AppendLine($"{n++}. `cd infra/cloudformation && ./deploy.sh hml sa-east-1` (ou `.\\deploy.ps1 -Environment hml`): cria as stacks na ordem; repita para atualizar.");
        if (framework)
        {
            sb.AppendLine($"{n++}. **Deploy da aplicação**: o workflow `.github/workflows/deploy.yml` compila com MSBuild num runner Windows, empacota com `infra/codedeploy/<projeto>/` (appspec + scripts) e publica pelo CodeDeploy. Manual: `aws deploy push` + `aws deploy create-deployment` (comandos no workflow).");
            if (result.Databases.Count > 0) sb.AppendLine($"{n++}. **Dados**: restaure o backup no RDS (`.bak` no S3 + `rds_restore_database`, ou AWS DMS), crie o usuário da aplicação e atualize o segredo da connection string.");
            if (components.Contains("fsx")) sb.AppendLine($"{n++}. **Arquivos**: informe `ActiveDirectoryId` na stack de storage, monte `\\\\<FsxDnsName>\\share` com os mesmos nomes de pasta e copie com robocopy/DataSync; ou configure um File Gateway sobre o bucket.");
            sb.AppendLine($"{n++}. **DNS**: aponte os hosts (`Host<Projeto>` nos parâmetros) para `AlbDnsName` (Route 53 alias) e informe `CertificateArn` (ACM) para HTTPS.");
        }
        else
        {
            sb.AppendLine($"{n++}. **Imagens**: a stack cria os repositórios ECR; publique as imagens (workflow ou `docker build -f <proj>/Dockerfile .` + `docker push`) e rode `aws ecs update-service --force-new-deployment`.");
            if (lambdas.Count > 0) sb.AppendLine($"{n++}. **Lambda**: `dotnet lambda package` em cada projeto Lambda, `aws s3 cp` do .zip para `PackageBucket`/`PackageKey<Projeto>` e deploy da stack 40-lambda; depois o workflow atualiza o código com `dotnet lambda deploy-function`.");
            if (result.Databases.Count > 0) sb.AppendLine($"{n++}. **Dados**: restaure o backup no RDS (`.bak` no S3 + `rds_restore_database`, ou AWS DMS) e crie o usuário da aplicação; atualize o segredo da connection string.");
            sb.AppendLine($"{n++}. **DNS**: aponte os hosts (`Host<Projeto>`) para `AlbDnsName` (Route 53 alias) e informe `CertificateArn` (ACM) para HTTPS.");
        }
        sb.AppendLine();
        sb.AppendLine("## Antes de produção");
        sb.AppendLine();
        sb.AppendLine("- Segundo NAT Gateway (zona B)" + (framework ? " e `WebDesiredCapacity >= 2`." : " e `DesiredCount >= 2` nos serviços web."));
        sb.AppendLine("- AWS WAF no ALB para aplicações públicas; access logs do ALB em S3.");
        sb.AppendLine("- Retenção dos log groups e budget/alertas de custo.");
        sb.AppendLine("- Revisar as políticas IAM: estão restritas ao prefixo da aplicação, mas S3/SQS/SES podem ser apertados por recurso.");
        if (framework) sb.AppendLine("- Trocar o user data por uma AMI do EC2 Image Builder (IIS, .NET 4.8.1, agentes já instalados) para instâncias subirem em minutos; validar `cfn-lint`/`aws cloudformation validate-template` após qualquer edição.");
        sb.AppendLine("- Validar os templates com `cfn-lint infra/cloudformation/*.yaml` após qualquer edição.");
        return Yaml(sb);
    }

    // ------------------------------------------------------------------ helpers

    private static string Yaml(StringBuilder sb) => sb.ToString().Replace("\r\n", "\n");

    private static string Slug(string name) => Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9-]+", "-").Trim('-');

    private static string Id(string name) => Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9_]+", "_").Trim('_');

    /// <summary>CloudFormation logical IDs are alphanumeric: "LegacyShop.Web" → "LegacyShopWeb".</summary>
    internal static string Logical(string name)
    {
        var sb = new StringBuilder();
        var upper = true;
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c)) { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return sb.ToString();
    }

    private static string DefaultCultureTimezone(ApplicationProfile profile) =>
        profile.Culture?.StartsWith("pt-BR", StringComparison.OrdinalIgnoreCase) == true || profile.Has(Signal.DateTimeNow) ? "E. South America Standard Time" : "UTC";
}
