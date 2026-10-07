using System.Text;
using System.Text.RegularExpressions;
using Migrator.Core.Analysis;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>
/// .NET 10 target, CloudFormation in the "one service per template" layout used by the portfolio's platform team:
/// <c>infra/service.yml</c> (ECS service: log group, metric filter + alarm, security group, task definition, service,
/// auto scaling) with VPC, subnets, cluster, roles and the shared ALB listener arriving as parameters, and
/// <c>infra/{dev,hom,prod}/parameters.json</c> in the CodePipeline template-configuration shape
/// (<c>{"Parameters": {...}}</c>) holding every environment-specific value, URLs and e-mails included. Resources the
/// application owns (queues, bucket, RDS, secret names) go to <c>infra/data.yml</c>; Lambdas to <c>infra/lambda-&lt;name&gt;.yml</c>.
/// </summary>
public static partial class CloudFormationGenerator
{
    // ------------------------------------------------------------------ service.yml

    // ------------------------------------------------------------------ lambda-<micro>.yml

    private static string LambdaTemplate(SolutionResult result, Deployable l, string micro, bool hasDb, bool hasS3)
    {
        var ns = string.IsNullOrWhiteSpace(l.Result.Project.RootNamespace) ? l.Result.Project.Name : l.Result.Project.RootNamespace;
        var assembly = string.IsNullOrWhiteSpace(l.Result.Project.AssemblyName) ? l.Result.Project.Name : l.Result.Project.AssemblyName;
        var fileDriven = FileDriven(l.Profile);
        var handler = fileDriven ? "FunctionHandler" : "QueueHandler";
        var mailbox = l.Profile.Has(Signal.MailboxReading);
        var settings = SettingsFor([l]);
        var secrets = SecretsFor(l);
        var sb = new StringBuilder();
        sb.AppendLine("AWSTemplateFormatVersion: \"2010-09-09\"");
        sb.AppendLine($"Description: \"Lambda {l.Result.Project.Name} ({result.SolutionName}): automação orientada a evento. Gerado pelo Migrator.\"");
        sb.AppendLine();
        sb.AppendLine("Parameters:");
        sb.AppendLine($$"""
              Projeto:
                Type: String
                Default: "{{result.SolutionName}}"
            """);
        sb.AppendLine(PipelineBlock);
        sb.AppendLine("""
              Environment:
                Type: String
                Default: dev
                AllowedValues: [dev, hom, prod]
              VPCID:
                Type: String
              PrivateSubnetOne:
                Type: String
              PrivateSubnetTwo:
                Type: String
              PrivateSubnetThree:
                Type: String
              LambdaRoleArn:
                Description: Role da função (logs, VPC, segredos do prefixo, S3/SQS). Vazio = criar uma role nesta stack.
                Type: String
                Default: ""
              LambdaRuntime:
                Type: String
                Default: dotnet10
              PackageBucket:
                Description: Bucket com o pacote .zip (saída de 'dotnet lambda package'); a pipeline atualiza o código depois.
                Type: String
              PackageKey:
                Type: String
            """);
        sb.AppendLine($"    Default: lambda/{micro}.zip");
        if (mailbox)
            sb.AppendLine("""
                  MailboxPollSchedule:
                    Description: Frequência com que a função consulta a caixa postal (Microsoft Graph/IMAP) enquanto o recebimento pelo SES não estiver configurado.
                    Type: String
                    Default: rate(5 minutes)
                  EnableSesInbound:
                    Type: String
                    Default: "false"
                    AllowedValues: ["true", "false"]
                  InboundRecipients:
                    Description: "Endereços/domínios recebidos pelo SES (ex.: pedidos@exemplo.com.br)."
                    Type: CommaDelimitedList
                    Default: ""
                """);
        foreach (var (setting, parameter, _) in settings)
        {
            sb.AppendLine($"  {parameter}:");
            sb.AppendLine($"    Description: \"{(setting.Kind == SettingKind.Url ? "URL" : "E-mail")} {setting.Key} ({(setting.Source == SettingSource.Code ? "estava fixo no código" : "appSettings")})\"");
            sb.AppendLine("    Type: String");
            sb.AppendLine($"    Default: \"{setting.Value.Replace("\"", "'")}\"");
        }
        sb.AppendLine();
        sb.AppendLine("Conditions:");
        sb.AppendLine("  CreateRole: !Equals [!Ref LambdaRoleArn, \"\"]");
        if (mailbox) sb.AppendLine("  SesInbound: !Equals [!Ref EnableSesInbound, \"true\"]");
        sb.AppendLine();
        sb.AppendLine("""
            Resources:
              LambdaRole:
                Type: AWS::IAM::Role
                Condition: CreateRole
                Properties:
                  AssumeRolePolicyDocument:
                    Version: "2012-10-17"
                    Statement: [{ Effect: Allow, Principal: { Service: lambda.amazonaws.com }, Action: sts:AssumeRole }]
                  ManagedPolicyArns:
                    - arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole
                    - arn:aws:iam::aws:policy/service-role/AWSLambdaVPCAccessExecutionRole
                  Policies:
                    - PolicyName: aplicacao
                      PolicyDocument:
                        Version: "2012-10-17"
                        Statement:
                          - Effect: Allow
                            Action: [secretsmanager:GetSecretValue]
                            Resource: !Sub "arn:aws:secretsmanager:${AWS::Region}:${AWS::AccountId}:secret:${FeatureName}/*"
                          - Effect: Allow
                            Action: [sqs:SendMessage, sqs:ReceiveMessage, sqs:DeleteMessage, sqs:GetQueueAttributes, sqs:ChangeMessageVisibility]
                            Resource: !Sub "arn:aws:sqs:${AWS::Region}:${AWS::AccountId}:${FeatureName}-${Environment}-*"
            """);
        if (hasS3)
            sb.AppendLine("""
                              - Effect: Allow
                                Action: [s3:GetObject, s3:PutObject, s3:DeleteObject, s3:ListBucket]
                                Resource:
                                  - !ImportValue { "Fn::Sub": "${FeatureName}-${Environment}-FilesBucketArn" }
                                  - !Sub
                                    - "${BucketArn}/*"
                                    - BucketArn: !ImportValue { "Fn::Sub": "${FeatureName}-${Environment}-FilesBucketArn" }
                """);
        sb.AppendLine("""
              SecurityGroup:
                Type: AWS::EC2::SecurityGroup
                Properties:
                  GroupDescription: !Sub "${FeatureName}-${MicroServiceName}"
                  VpcId: !Ref VPCID
            """);
        if (hasDb)
            sb.AppendLine("""
                  DbIngress:
                    Type: AWS::EC2::SecurityGroupIngress
                    Properties:
                      GroupId: !ImportValue { "Fn::Sub": "${FeatureName}-${Environment}-DbSecurityGroupId" }
                      IpProtocol: tcp
                      FromPort: !ImportValue { "Fn::Sub": "${FeatureName}-${Environment}-DbPort" }
                      ToPort: !ImportValue { "Fn::Sub": "${FeatureName}-${Environment}-DbPort" }
                      SourceSecurityGroupId: !Ref SecurityGroup
                """);
        sb.AppendLine($$"""
              Dlq:
                Type: AWS::SQS::Queue
                Properties:
                  QueueName: !Sub "${FeatureName}-${Environment}-${MicroServiceName}-dlq"
              CloudWatchLogGroup:
                Type: AWS::Logs::LogGroup
                Properties:
                  LogGroupName: !Sub "/aws/lambda/${FeatureName}-${Environment}-${MicroServiceName}"
                  RetentionInDays: 30
              Function:
                Type: AWS::Lambda::Function
                DependsOn: CloudWatchLogGroup
                Properties:
                  Tags:
                    - { Key: Projeto, Value: !Ref Projeto }
                    - { Key: Environment, Value: !Ref Environment }
                    - { Key: DevToolsAccount, Value: !Ref DevToolsAccount }
                  FunctionName: !Sub "${FeatureName}-${Environment}-${MicroServiceName}"
                  Runtime: !Ref LambdaRuntime
                  Architectures: [arm64]
                  Handler: "{{assembly}}::{{ns}}.Function::{{handler}}"
                  Role: !If [CreateRole, !GetAtt LambdaRole.Arn, !Ref LambdaRoleArn]
                  Code: { S3Bucket: !Ref PackageBucket, S3Key: !Ref PackageKey }
                  Timeout: 300
                  MemorySize: 512
                  DeadLetterConfig: { TargetArn: !GetAtt Dlq.Arn }
                  VpcConfig:
                    SubnetIds: [!Ref PrivateSubnetOne, !Ref PrivateSubnetTwo, !Ref PrivateSubnetThree]
                    SecurityGroupIds: [!Ref SecurityGroup]
                  Environment:
                    Variables:
                      DOTNET_ENVIRONMENT: Production
                      ENVIRONMENT: !Ref Environment
            """);
        if (hasS3) sb.AppendLine("          FILES_BUCKET: !ImportValue { \"Fn::Sub\": \"${FeatureName}-${Environment}-FilesBucketName\" }");
        foreach (var (setting, parameter, _) in settings) sb.AppendLine($"          {setting.EnvironmentVariable}: !Ref {parameter} # {setting.Key}");
        foreach (var s in secrets) sb.AppendLine($"          {s.EnvironmentVariable}_SECRET: \"{s.SecretName}\" # leia com AWSSDK.SecretsManager (Lambda não injeta segredos como o ECS)");
        if (fileDriven && hasS3)
            sb.AppendLine("""
                  FilesTrigger:
                    Type: AWS::Lambda::EventSourceMapping
                    Properties:
                      FunctionName: !Ref Function
                      EventSourceArn: !ImportValue { "Fn::Sub": "${FeatureName}-${Environment}-FilesEventsQueueArn" }
                      BatchSize: 1
                      FunctionResponseTypes: [ReportBatchItemFailures]
                """);
        if (!fileDriven)
            sb.AppendLine("""
                  Queue:
                    Type: AWS::SQS::Queue
                    Properties:
                      QueueName: !Sub "${FeatureName}-${Environment}-${MicroServiceName}"
                      VisibilityTimeout: 360
                      RedrivePolicy: { deadLetterTargetArn: !GetAtt Dlq.Arn, maxReceiveCount: 5 }
                  QueueTrigger:
                    Type: AWS::Lambda::EventSourceMapping
                    Properties:
                      FunctionName: !Ref Function
                      EventSourceArn: !GetAtt Queue.Arn
                      BatchSize: 10
                      FunctionResponseTypes: [ReportBatchItemFailures]
                """);
        if (mailbox)
            sb.AppendLine($$"""
                  SchedulerRole:
                    Type: AWS::IAM::Role
                    Properties:
                      AssumeRolePolicyDocument:
                        Version: "2012-10-17"
                        Statement: [{ Effect: Allow, Principal: { Service: scheduler.amazonaws.com }, Action: sts:AssumeRole }]
                      Policies:
                        - PolicyName: invoke
                          PolicyDocument:
                            Version: "2012-10-17"
                            Statement:
                              - Effect: Allow
                                Action: [lambda:InvokeFunction]
                                Resource: !GetAtt Function.Arn
                  # Enquanto a caixa continuar no Exchange/M365: a função é chamada a cada intervalo e consulta a caixa (Microsoft Graph/IMAP).
                  MailboxSchedule:
                    Type: AWS::Scheduler::Schedule
                    Properties:
                      Name: !Sub "${FeatureName}-${Environment}-${MicroServiceName}-caixa-postal"
                      ScheduleExpression: !Ref MailboxPollSchedule
                      FlexibleTimeWindow: { Mode: "OFF" }
                      Target:
                        Arn: !GetAtt Function.Arn
                        RoleArn: !GetAtt SchedulerRole.Arn
                        Input: '{"source":"scheduler","action":"poll-mailbox"}'
                  # E-mail recebido pelo SES vira objeto no bucket (prefixo {{l.Slug}}/entrada/email/) e o evento do S3 dispara a função.
                  ReceiptRuleSet:
                    Type: AWS::SES::ReceiptRuleSet
                    Condition: SesInbound
                    Properties:
                      RuleSetName: !Sub "${FeatureName}-${Environment}-${MicroServiceName}"
                  ReceiptRule:
                    Type: AWS::SES::ReceiptRule
                    Condition: SesInbound
                    Properties:
                      RuleSetName: !Ref ReceiptRuleSet
                      Rule:
                        Name: !Sub "${MicroServiceName}-para-s3"
                        Enabled: true
                        ScanEnabled: true
                        Recipients: !Ref InboundRecipients
                        Actions:
                          - S3Action:
                              BucketName: !ImportValue { "Fn::Sub": "${FeatureName}-${Environment}-FilesBucketName" }
                              ObjectKeyPrefix: {{l.Slug}}/entrada/email/
                """);
        sb.AppendLine("""
              ErrorsAlarm:
                Type: AWS::CloudWatch::Alarm
                Properties:
                  AlarmName: !Sub "Alarm-${FeatureName}-${MicroServiceName}-errors"
                  Namespace: AWS/Lambda
                  MetricName: Errors
                  Dimensions: [{ Name: FunctionName, Value: !Ref Function }]
                  Statistic: Sum
                  Period: 300
                  EvaluationPeriods: 1
                  Threshold: 1
                  ComparisonOperator: GreaterThanOrEqualToThreshold
                  TreatMissingData: notBreaching
                  AlarmActions:
                    - "{{resolve:ssm:/org/member/workload_local_sns_arn:1}}"

            Outputs:
              FunctionName:
                Value: !Ref Function
            """);
        return Yaml(sb);
    }

    // ------------------------------------------------------------------ data.yml (what the application owns)

    private static string DataTemplate(SolutionResult result, bool hasDb, bool hasS3, bool hasFsx, bool framework, List<Deployable> fileAutomations, List<Deployable> workers, List<(string Name, string Description)> secrets)
    {
        var engine = DefaultEngine(result);
        var sqlServer = engine.StartsWith("sqlserver", StringComparison.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("AWSTemplateFormatVersion: \"2010-09-09\"");
        sb.AppendLine($"Description: \"Recursos próprios de {result.SolutionName}: {(hasDb ? "RDS, " : "")}{(hasS3 ? "bucket S3, " : "")}{(hasFsx ? "FSx for Windows, " : "")}{(framework ? "bucket de artefatos do CodeDeploy, " : "")}{(workers.Count > 0 || fileAutomations.Count > 0 ? "filas SQS, " : "")}{(secrets.Count > 0 ? "nomes dos segredos, " : "")}exportados para os templates de serviço. Gerado pelo Migrator.\"");
        sb.AppendLine();
        sb.AppendLine("""
            Parameters:
              FeatureName:
                Type: String
                AllowedPattern: "[a-z]*"
              Environment:
                Type: String
                Default: dev
                AllowedValues: [dev, hom, prod]
            """);
        if (hasDb || hasFsx)
            sb.AppendLine("""
                  VPCID:
                    Type: String
                  PrivateSubnetOne:
                    Type: String
                  PrivateSubnetTwo:
                    Type: String
                  PrivateSubnetThree:
                    Type: String
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
                  FsxThroughputCapacity:
                    Type: Number
                    Default: 32
                """);
        if (hasDb)
            sb.AppendLine($$"""
                  DbEngine:
                    Type: String
                    Default: {{engine}}
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
                """);
        sb.AppendLine();
        sb.AppendLine("Conditions:");
        sb.AppendLine("  IsProd: !Equals [!Ref Environment, prod]");
        if (hasDb) sb.AppendLine("  HasLicenseModel: !Not [!Equals [!Ref LicenseModel, \"\"]]");
        if (hasFsx) sb.AppendLine("  HasActiveDirectory: !Not [!Equals [!Ref ActiveDirectoryId, \"\"]]");
        sb.AppendLine();
        sb.AppendLine("Resources:");
        if (framework)
            sb.AppendLine("""
                  # Pacotes do CodeDeploy (zip gerado pela esteira).
                  ArtifactsBucket:
                    Type: AWS::S3::Bucket
                    Properties:
                      BucketName: !Sub "${FeatureName}-${Environment}-${AWS::AccountId}-deploy"
                      PublicAccessBlockConfiguration: { BlockPublicAcls: true, BlockPublicPolicy: true, IgnorePublicAcls: true, RestrictPublicBuckets: true }
                      VersioningConfiguration: { Status: Enabled }
                      LifecycleConfiguration:
                        Rules: [{ Id: expira-pacotes, Status: Enabled, ExpirationInDays: 90 }]
                """);
        if (hasFsx)
            sb.AppendLine("""
                  # Pastas de rede (\\servidor\pasta) com o mesmo caminho SMB, sem mudar o código. Copie os dados com robocopy ou AWS DataSync.
                  FsxSecurityGroup:
                    Type: AWS::EC2::SecurityGroup
                    Condition: HasActiveDirectory
                    Properties:
                      GroupDescription: FSx for Windows (SMB)
                      VpcId: !Ref VPCID
                      SecurityGroupIngress:
                        - { IpProtocol: tcp, FromPort: 445, ToPort: 445, CidrIp: 10.0.0.0/8 }
                        - { IpProtocol: tcp, FromPort: 135, ToPort: 135, CidrIp: 10.0.0.0/8 }
                        - { IpProtocol: tcp, FromPort: 49152, ToPort: 65535, CidrIp: 10.0.0.0/8 }
                  FileShare:
                    Type: AWS::FSx::FileSystem
                    Condition: HasActiveDirectory
                    Properties:
                      FileSystemType: WINDOWS
                      StorageType: SSD
                      StorageCapacity: !Ref FsxStorageCapacity
                      SubnetIds: [!Ref PrivateSubnetOne]
                      SecurityGroupIds: [!Ref FsxSecurityGroup]
                      WindowsConfiguration:
                        ActiveDirectoryId: !Ref ActiveDirectoryId
                        ThroughputCapacity: !Ref FsxThroughputCapacity
                        DeploymentType: SINGLE_AZ_2 # MULTI_AZ_1 em produção (dobra o custo; exige PreferredSubnetId)
                        AutomaticBackupRetentionDays: 7
                      Tags: [{ Key: Name, Value: !Sub "${FeatureName}-${Environment}-files" }]
                """);
        if (hasDb)
            sb.AppendLine("""
                  DbSubnetGroup:
                    Type: AWS::RDS::DBSubnetGroup
                    Properties:
                      DBSubnetGroupDescription: !Sub "${FeatureName}-${Environment}"
                      SubnetIds: [!Ref PrivateSubnetOne, !Ref PrivateSubnetTwo, !Ref PrivateSubnetThree]
                  DbSecurityGroup:
                    Type: AWS::EC2::SecurityGroup
                    Properties:
                      GroupDescription: RDS (a porta e liberada por cada servico a partir do seu security group)
                      VpcId: !Ref VPCID
                  Database:
                    Type: AWS::RDS::DBInstance
                    DeletionPolicy: Snapshot
                    UpdateReplacePolicy: Snapshot
                    Properties:
                      DBInstanceIdentifier: !Sub "${FeatureName}-${Environment}"
                      Engine: !Ref DbEngine
                      DBInstanceClass: !Ref DbInstanceClass
                      AllocatedStorage: !Ref DbAllocatedStorage
                      StorageType: gp3
                      StorageEncrypted: true
                      Port: !Ref DbPort
                      DBSubnetGroupName: !Ref DbSubnetGroup
                      VPCSecurityGroups: [!Ref DbSecurityGroup]
                      MasterUsername: !Ref DbUsername
                      ManageMasterUserPassword: true
                      MultiAZ: !If [IsProd, true, false]
                      BackupRetentionPeriod: 7
                      DeletionProtection: !If [IsProd, true, false]
                      PubliclyAccessible: false
                      LicenseModel: !If [HasLicenseModel, !Ref LicenseModel, !Ref "AWS::NoValue"]
                """);
        if (hasS3)
        {
            if (fileAutomations.Count > 0)
                sb.AppendLine("""
                      FilesEventsDlq:
                        Type: AWS::SQS::Queue
                        Properties:
                          QueueName: !Sub "${FeatureName}-${Environment}-files-events-dlq"
                      FilesEventsQueue:
                        Type: AWS::SQS::Queue
                        Properties:
                          QueueName: !Sub "${FeatureName}-${Environment}-files-events"
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
                                  ArnLike: { "aws:SourceArn": !Sub "arn:aws:s3:::${FeatureName}-${Environment}-${AWS::AccountId}-files" }
                                  StringEquals: { "aws:SourceAccount": !Ref "AWS::AccountId" }
                    """);
            sb.AppendLine("  FilesBucket:");
            sb.AppendLine("    Type: AWS::S3::Bucket");
            if (fileAutomations.Count > 0) sb.AppendLine("    DependsOn: FilesEventsQueuePolicy");
            sb.AppendLine("""
                    Properties:
                      BucketName: !Sub "${FeatureName}-${Environment}-${AWS::AccountId}-files"
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
                    sb.AppendLine($"          - Event: s3:ObjectCreated:*\n            Queue: !GetAtt FilesEventsQueue.Arn\n            Filter:\n              S3Key:\n                Rules: [{{ Name: prefix, Value: {l.Slug}/entrada/ }}]");
            }
        }
        foreach (var w in workers)
            sb.AppendLine($$"""
                  {{w.Logical}}Dlq:
                    Type: AWS::SQS::Queue
                    Properties:
                      QueueName: !Sub "${FeatureName}-${Environment}-{{w.Slug}}-dlq"
                  {{w.Logical}}Queue:
                    Type: AWS::SQS::Queue
                    Properties:
                      QueueName: !Sub "${FeatureName}-${Environment}-{{w.Slug}}"
                      VisibilityTimeout: 300
                      RedrivePolicy:
                        deadLetterTargetArn: !GetAtt {{w.Logical}}Dlq.Arn
                        maxReceiveCount: 5
                """);
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (secrets.Count > 0) sb.AppendLine("  # Segredos: só o nome e a descrição; o valor entra por _secrets/<projeto>/create-secrets.sh (put-secret-value), nunca pelo template.");
        foreach (var (name, description) in secrets)
        {
            var logical = "Secret" + Logical(name);
            while (!used.Add(logical)) logical += "X";
            sb.AppendLine($"  {logical}:");
            sb.AppendLine("    Type: AWS::SecretsManager::Secret");
            sb.AppendLine("    Properties:");
            sb.AppendLine($"      Name: \"{name}\"");
            sb.AppendLine($"      Description: \"{description.Replace("\"", "'")}\"");
            sb.AppendLine("      SecretString: \"PREENCHER\"");
            sb.AppendLine("      Tags: [{ Key: Application, Value: !Ref FeatureName }, { Key: Environment, Value: !Ref Environment }]");
        }
        sb.AppendLine();
        sb.AppendLine("Outputs:");
        if (framework)
            sb.AppendLine("""
                  ArtifactsBucketName:
                    Value: !Ref ArtifactsBucket
                    Export: { Name: !Sub "${FeatureName}-${Environment}-ArtifactsBucketName" }
                  ArtifactsBucketArn:
                    Value: !GetAtt ArtifactsBucket.Arn
                    Export: { Name: !Sub "${FeatureName}-${Environment}-ArtifactsBucketArn" }
                """);
        if (hasFsx)
            sb.AppendLine("""
                  FsxDnsName:
                    Condition: HasActiveDirectory
                    Value: !GetAtt FileShare.DNSName
                    Export: { Name: !Sub "${FeatureName}-${Environment}-FsxDnsName" }
                """);
        if (hasDb)
            sb.AppendLine("""
                  DbEndpoint:
                    Value: !GetAtt Database.Endpoint.Address
                    Export: { Name: !Sub "${FeatureName}-${Environment}-DbEndpoint" }
                  DbPort:
                    Value: !Ref DbPort
                    Export: { Name: !Sub "${FeatureName}-${Environment}-DbPort" }
                  DbSecurityGroupId:
                    Value: !Ref DbSecurityGroup
                    Export: { Name: !Sub "${FeatureName}-${Environment}-DbSecurityGroupId" }
                  DbMasterSecretArn:
                    Value: !GetAtt Database.MasterUserSecret.SecretArn
                """);
        if (hasS3)
            sb.AppendLine("""
                  FilesBucketName:
                    Value: !Ref FilesBucket
                    Export: { Name: !Sub "${FeatureName}-${Environment}-FilesBucketName" }
                  FilesBucketArn:
                    Value: !GetAtt FilesBucket.Arn
                    Export: { Name: !Sub "${FeatureName}-${Environment}-FilesBucketArn" }
                """);
        if (fileAutomations.Count > 0)
            sb.AppendLine("""
                  FilesEventsQueueArn:
                    Value: !GetAtt FilesEventsQueue.Arn
                    Export: { Name: !Sub "${FeatureName}-${Environment}-FilesEventsQueueArn" }
                """);
        foreach (var w in workers)
            sb.AppendLine($$"""
                  {{w.Logical}}QueueUrl:
                    Value: !Ref {{w.Logical}}Queue
                    Export: { Name: !Sub "${FeatureName}-${Environment}-{{w.Logical}}QueueUrl" }
                """);
        if (!hasDb && !hasS3 && !framework && !hasFsx && fileAutomations.Count == 0 && workers.Count == 0)
            sb.AppendLine("  SecretsCreated:\n    Value: !Sub \"${FeatureName}-${Environment}\"");
        return Yaml(sb);
    }

    // ------------------------------------------------------------------ <env>/parameters*.json

    private static List<(string Key, string Value)> LambdaParameters(SolutionResult result, Deployable l, string environment, string feature)
    {
        var micro = Micro(result, l);
        var list = new List<(string, string)>
        {
            ("FeatureName", feature), ("MicroServiceName", micro), ("DevToolsAccount", "123456789012"), ("Environment", environment),
            ("VPCID", "vpc-xxxxxxxxxxxxxxxxx"), ("PrivateSubnetOne", "subnet-xxxxxxxxxxxxxxxx1"), ("PrivateSubnetTwo", "subnet-xxxxxxxxxxxxxxxx2"), ("PrivateSubnetThree", "subnet-xxxxxxxxxxxxxxxx3"),
            ("LambdaRoleArn", ""), ("LambdaRuntime", "dotnet10"), ("PackageBucket", $"{feature}-{environment}-artifacts"), ("PackageKey", $"lambda/{micro}.zip")
        };
        if (l.Profile.Has(Signal.MailboxReading)) { list.Add(("MailboxPollSchedule", "rate(5 minutes)")); list.Add(("EnableSesInbound", "false")); list.Add(("InboundRecipients", "")); }
        foreach (var (setting, parameter, _) in SettingsFor([l])) list.Add((parameter, SettingValue(setting, environment)));
        return list;
    }

    private static string ServiceWorkflow(SolutionResult result, string feature, List<Deployable> services, List<Deployable> lambdas)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Gerado pelo Migrator: build, testes, imagens no ECR (<feature>-<microservico>, linux/arm64) e update-service no ECS para {result.SolutionName}.");
        sb.AppendLine("# Pré-requisitos: role IAM para GitHub OIDC em AWS_ROLE_ARN (secret), stacks criadas com infra/deploy.sh. Se a plataforma usar a própria esteira, este arquivo serve de referência dos comandos.");
        sb.AppendLine("name: deploy");
        sb.AppendLine();
        sb.AppendLine("on:");
        sb.AppendLine("  push:");
        sb.AppendLine("    branches: [main]");
        sb.AppendLine("  workflow_dispatch:");
        sb.AppendLine();
        sb.AppendLine("permissions:");
        sb.AppendLine("  id-token: write");
        sb.AppendLine("  contents: read");
        sb.AppendLine();
        sb.AppendLine("env:");
        sb.AppendLine("  AWS_REGION: sa-east-1");
        sb.AppendLine($"  FEATURE_NAME: {feature}");
        sb.AppendLine("  ENVIRONMENT: prod");
        sb.AppendLine("  DOTNET_VERSION: 10.0.x");
        sb.AppendLine();
        sb.AppendLine("jobs:");
        sb.AppendLine("  build-test:");
        sb.AppendLine("    runs-on: ubuntu-latest");
        sb.AppendLine("    steps:");
        sb.AppendLine("      - uses: actions/checkout@v4");
        sb.AppendLine("      - uses: actions/setup-dotnet@v4");
        sb.AppendLine("        with:");
        sb.AppendLine("          dotnet-version: ${{ env.DOTNET_VERSION }}");
        sb.AppendLine("      - run: dotnet restore " + SourceDir);
        sb.AppendLine("      - run: dotnet build " + SourceDir + " --no-restore -c Release");
        sb.AppendLine("      - run: dotnet test " + SourceDir + " --no-build -c Release");
        if (services.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  containers:");
            sb.AppendLine("    needs: build-test");
            sb.AppendLine("    runs-on: ubuntu-latest");
            sb.AppendLine("    strategy:");
            sb.AppendLine("      fail-fast: false");
            sb.AppendLine("      matrix:");
            sb.AppendLine("        include:");
            foreach (var d in services)
            {
                sb.AppendLine($"          - micro: {Micro(result, d)}");
                sb.AppendLine($"            dockerfile: {d.Result.RelativeDir.Replace('\\', '/')}/Dockerfile");
                sb.AppendLine($"            kind: {(d.Result.Hosting!.Primary == AwsHosting.EcsScheduledTask ? "task" : "service")}");
            }
            sb.AppendLine("    steps:");
            sb.AppendLine("      - uses: actions/checkout@v4");
            sb.AppendLine("      - uses: docker/setup-qemu-action@v3");
            sb.AppendLine("      - uses: docker/setup-buildx-action@v3");
            sb.AppendLine("      - uses: aws-actions/configure-aws-credentials@v4");
            sb.AppendLine("        with:");
            sb.AppendLine("          role-to-assume: ${{ secrets.AWS_ROLE_ARN }}");
            sb.AppendLine("          aws-region: ${{ env.AWS_REGION }}");
            sb.AppendLine("      - id: ecr");
            sb.AppendLine("        uses: aws-actions/amazon-ecr-login@v2");
            sb.AppendLine("      - name: Build e push (linux/arm64)");
            sb.AppendLine("        run: |");
            sb.AppendLine("          IMAGE=${{ steps.ecr.outputs.registry }}/${{ env.FEATURE_NAME }}-${{ matrix.micro }}-${{ env.ENVIRONMENT }}");
            sb.AppendLine("          docker buildx build --platform linux/arm64 -f " + SourceDir + "/${{ matrix.dockerfile }} -t $IMAGE:${{ github.sha }} -t $IMAGE:latest --push " + SourceDir);
            sb.AppendLine("      - name: Deploy no ECS");
            sb.AppendLine("        if: matrix.kind == 'service'");
            sb.AppendLine("        run: |");
            sb.AppendLine("          aws ecs update-service --cluster ecs-cluster-${{ env.FEATURE_NAME }}-fargate --service service-${{ env.FEATURE_NAME }}-${{ matrix.micro }} --force-new-deployment");
            sb.AppendLine("          aws ecs wait services-stable --cluster ecs-cluster-${{ env.FEATURE_NAME }}-fargate --services service-${{ env.FEATURE_NAME }}-${{ matrix.micro }}");
            sb.AppendLine("      - name: Tarefa agendada atualizada");
            sb.AppendLine("        if: matrix.kind == 'task'");
            sb.AppendLine("        run: echo \"A próxima execução do EventBridge Scheduler usa a imagem :latest recém-publicada.\"");
        }
        if (lambdas.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  lambdas:");
            sb.AppendLine("    needs: build-test");
            sb.AppendLine("    runs-on: ubuntu-latest");
            sb.AppendLine("    strategy:");
            sb.AppendLine("      matrix:");
            sb.AppendLine("        include:");
            foreach (var l in lambdas)
            {
                sb.AppendLine($"          - micro: {Micro(result, l)}");
                sb.AppendLine($"            project: {l.Result.OutputProjectPath!.Replace('\\', '/')}");
            }
            sb.AppendLine("    steps:");
            sb.AppendLine("      - uses: actions/checkout@v4");
            sb.AppendLine("      - uses: actions/setup-dotnet@v4");
            sb.AppendLine("        with:");
            sb.AppendLine("          dotnet-version: ${{ env.DOTNET_VERSION }}");
            sb.AppendLine("      - uses: aws-actions/configure-aws-credentials@v4");
            sb.AppendLine("        with:");
            sb.AppendLine("          role-to-assume: ${{ secrets.AWS_ROLE_ARN }}");
            sb.AppendLine("          aws-region: ${{ env.AWS_REGION }}");
            sb.AppendLine("      - run: dotnet tool install -g Amazon.Lambda.Tools");
            sb.AppendLine("      - name: Package e deploy");
            sb.AppendLine("        run: |");
            sb.AppendLine("          cd " + SourceDir + "/$(dirname ${{ matrix.project }})");
            sb.AppendLine("          dotnet lambda deploy-function ${{ env.FEATURE_NAME }}-${{ env.ENVIRONMENT }}-${{ matrix.micro }} --region ${{ env.AWS_REGION }} --function-architecture arm64");
        }
        return Yaml(sb);
    }
}
