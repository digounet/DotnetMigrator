using System.Text;
using Migrator.Core.Analysis;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>ECS Fargate and Lambda stacks for the .NET 10 target (same shape as the Terraform module).</summary>
public static partial class CloudFormationGenerator
{
    private static List<ExtractedSecret> SecretsFor(Deployable d) =>
        d.Result.Secrets?.Secrets.Where(s => s.Environment is null or "Production").DistinctBy(s => s.EnvironmentVariable).ToList() ?? [];

    private static string ComputeEcs(SolutionResult result, List<Deployable> deployables, List<Deployable> webs, HashSet<string> components, bool hasDb, bool hasS3)
    {
        var containers = deployables.Where(d => d.Result.Hosting!.Primary != AwsHosting.Lambda).ToList();
        var scheduled = containers.Where(d => d.Result.Hosting!.Primary == AwsHosting.EcsScheduledTask).ToList();
        var workers = containers.Where(d => d.Result.Hosting!.Primary == AwsHosting.EcsFargateWorker).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("""
            AWSTemplateFormatVersion: "2010-09-09"
            Description: "Compute (ECS Fargate, ECR, ALB, tarefas agendadas, workers, alarmes). Gerado pelo Migrator."

            Parameters:
              AppName:
                Type: String
              Environment:
                Type: String
                Default: prod
                AllowedValues: [dev, hml, prod]
              ImageTag:
                Type: String
                Default: latest
                Description: Tag das imagens no ECR (o pipeline usa o SHA do commit).
              CertificateArn:
                Type: String
                Default: ""
              AlertEmail:
                Type: String
                Default: ""
              WebDesiredCount:
                Type: Number
                Default: 2
            """);
        foreach (var w in webs)
            sb.AppendLine($$"""
                  Host{{w.Logical}}:
                    Type: String
                    Default: {{w.Slug}}.exemplo.com.br
                """);
        foreach (var s in scheduled)
            sb.AppendLine($$"""
                  Schedule{{s.Logical}}:
                    Type: String
                    Default: rate(15 minutes)
                    Description: Expressão do EventBridge Scheduler (cron(...) ou rate(...)), em UTC.
                """);
        sb.AppendLine("""

            Conditions:
              HasCertificate: !Not [!Equals [!Ref CertificateArn, ""]]
              HasAlertEmail: !Not [!Equals [!Ref AlertEmail, ""]]

            Resources:
              AlertsTopic:
                Type: AWS::SNS::Topic
                Properties:
                  TopicName: !Sub "${AppName}-${Environment}-alerts"
              AlertsSubscription:
                Type: AWS::SNS::Subscription
                Condition: HasAlertEmail
                Properties: { TopicArn: !Ref AlertsTopic, Protocol: email, Endpoint: !Ref AlertEmail }
              Cluster:
                Type: AWS::ECS::Cluster
                Properties:
                  ClusterName: !Sub "${AppName}-${Environment}"
                  ClusterSettings: [{ Name: containerInsights, Value: enabled }]
              # Execution role: puxa a imagem do ECR, grava logs e lê segredos para injetar como variáveis de ambiente.
              ExecutionRole:
                Type: AWS::IAM::Role
                Properties:
                  RoleName: !Sub "${AppName}-${Environment}-ecs-execution"
                  AssumeRolePolicyDocument:
                    Version: "2012-10-17"
                    Statement: [{ Effect: Allow, Principal: { Service: ecs-tasks.amazonaws.com }, Action: sts:AssumeRole }]
                  ManagedPolicyArns: [arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy]
                  Policies:
                    - PolicyName: secrets
                      PolicyDocument:
                        Version: "2012-10-17"
                        Statement:
                          - Effect: Allow
                            Action: [secretsmanager:GetSecretValue, ssm:GetParameters, kms:Decrypt]
                            Resource: !Sub "arn:aws:secretsmanager:${AWS::Region}:${AWS::AccountId}:secret:${AppName}/*"
              # Task role: o que o código da aplicação pode fazer (sem access keys no código).
              TaskRole:
                Type: AWS::IAM::Role
                Properties:
                  RoleName: !Sub "${AppName}-${Environment}-ecs-task"
                  AssumeRolePolicyDocument:
                    Version: "2012-10-17"
                    Statement: [{ Effect: Allow, Principal: { Service: ecs-tasks.amazonaws.com }, Action: sts:AssumeRole }]
                  Policies:
                    - PolicyName: aplicacao
                      PolicyDocument:
                        Version: "2012-10-17"
                        Statement:
                          - Effect: Allow
                            Action: [ssm:GetParameter, ssm:GetParameters, ssm:GetParametersByPath, ssm:PutParameter]
                            Resource: !Sub "arn:aws:ssm:${AWS::Region}:${AWS::AccountId}:parameter/${AppName}/${Environment}/*"
                          - Effect: Allow
                            Action: [secretsmanager:GetSecretValue]
                            Resource: !Sub "arn:aws:secretsmanager:${AWS::Region}:${AWS::AccountId}:secret:${AppName}/*"
            """);
        if (hasS3)
            sb.AppendLine("""
                              - Effect: Allow
                                Action: [s3:GetObject, s3:PutObject, s3:DeleteObject, s3:ListBucket]
                                Resource:
                                  - !ImportValue { "Fn::Sub": "${AppName}-${Environment}-FilesBucketArn" }
                                  - !Sub
                                    - "${BucketArn}/*"
                                    - BucketArn: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-FilesBucketArn" }
                """);
        if (components.Contains("sqs") || components.Contains("s3-events") || workers.Count > 0)
            sb.AppendLine("""
                              - Effect: Allow
                                Action: [sqs:SendMessage, sqs:ReceiveMessage, sqs:DeleteMessage, sqs:GetQueueAttributes, sqs:ChangeMessageVisibility]
                                Resource: !Sub "arn:aws:sqs:${AWS::Region}:${AWS::AccountId}:${AppName}-${Environment}-*"
                """);
        if (components.Contains("ses") || components.Contains("ses-inbound"))
            sb.AppendLine("""
                              - Effect: Allow
                                Action: [ses:SendEmail, ses:SendRawEmail]
                                Resource: "*"
                """);
        sb.AppendLine("""
              TaskSecurityGroup:
                Type: AWS::EC2::SecurityGroup
                Properties:
                  GroupDescription: Tasks ECS
                  VpcId: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-VpcId" }
            """);
        if (webs.Count > 0)
            sb.AppendLine("""
                      SecurityGroupIngress:
                        - { IpProtocol: tcp, FromPort: 8080, ToPort: 8080, SourceSecurityGroupId: !Ref AlbSecurityGroup }
                  AlbSecurityGroup:
                    Type: AWS::EC2::SecurityGroup
                    Properties:
                      GroupDescription: ALB
                      VpcId: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-VpcId" }
                      SecurityGroupIngress:
                        - { IpProtocol: tcp, FromPort: 80, ToPort: 80, CidrIp: 0.0.0.0/0 }
                        - { IpProtocol: tcp, FromPort: 443, ToPort: 443, CidrIp: 0.0.0.0/0 }
                """);
        if (hasDb)
            sb.AppendLine("""
                  DbIngress:
                    Type: AWS::EC2::SecurityGroupIngress
                    Properties:
                      GroupId: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-DbSecurityGroupId" }
                      IpProtocol: tcp
                      FromPort: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-DbPort" }
                      ToPort: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-DbPort" }
                      SourceSecurityGroupId: !Ref TaskSecurityGroup
                """);
        if (webs.Count > 0)
        {
            sb.AppendLine($$"""
                  LoadBalancer:
                    Type: AWS::ElasticLoadBalancingV2::LoadBalancer
                    Properties:
                      Name: !Sub "${AppName}-${Environment}"
                      Scheme: internet-facing
                      Type: application
                      Subnets: !Split [",", !ImportValue { "Fn::Sub": "${AppName}-${Environment}-PublicSubnets" }]
                      SecurityGroups: [!Ref AlbSecurityGroup]
                  HttpListener:
                    Type: AWS::ElasticLoadBalancingV2::Listener
                    Properties:
                      LoadBalancerArn: !Ref LoadBalancer
                      Port: 80
                      Protocol: HTTP
                      DefaultActions: !If
                        - HasCertificate
                        - [{ Type: redirect, RedirectConfig: { Protocol: HTTPS, Port: "443", StatusCode: HTTP_301 } }]
                        - [{ Type: forward, TargetGroupArn: !Ref {{webs[0].Logical}}TargetGroup }]
                  HttpsListener:
                    Type: AWS::ElasticLoadBalancingV2::Listener
                    Condition: HasCertificate
                    Properties:
                      LoadBalancerArn: !Ref LoadBalancer
                      Port: 443
                      Protocol: HTTPS
                      Certificates: [{ CertificateArn: !Ref CertificateArn }]
                      SslPolicy: ELBSecurityPolicy-TLS13-1-2-2021-06
                      DefaultActions: [{ Type: forward, TargetGroupArn: !Ref {{webs[0].Logical}}TargetGroup }]
                  Alb5xxAlarm:
                    Type: AWS::CloudWatch::Alarm
                    Properties:
                      AlarmName: !Sub "${AppName}-${Environment}-alb-5xx"
                      Namespace: AWS/ApplicationELB
                      MetricName: HTTPCode_Target_5XX_Count
                      Dimensions: [{ Name: LoadBalancer, Value: !GetAtt LoadBalancer.LoadBalancerFullName }]
                      Statistic: Sum
                      Period: 300
                      EvaluationPeriods: 2
                      Threshold: 10
                      ComparisonOperator: GreaterThanThreshold
                      TreatMissingData: notBreaching
                      AlarmActions: [!Ref AlertsTopic]
                """);
            var priority = 10;
            foreach (var w in webs)
            {
                sb.AppendLine($$"""
                      {{w.Logical}}TargetGroup:
                        Type: AWS::ElasticLoadBalancingV2::TargetGroup
                        Properties:
                          Name: !Sub "${AppName}-${Environment}-{{Truncate(w.Slug, 14)}}"
                          VpcId: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-VpcId" }
                          Port: 8080
                          Protocol: HTTP
                          TargetType: ip
                          HealthCheckPath: /health
                          HealthCheckIntervalSeconds: 30
                          HealthyThresholdCount: 2
                          UnhealthyThresholdCount: 3
                          Matcher: { HttpCode: "200" }
                          TargetGroupAttributes: [{ Key: deregistration_delay.timeout_seconds, Value: "30" }]
                      {{w.Logical}}HttpRule:
                        Type: AWS::ElasticLoadBalancingV2::ListenerRule
                        Properties:
                          ListenerArn: !Ref HttpListener
                          Priority: {{priority}}
                          Conditions: [{ Field: host-header, Values: [!Ref Host{{w.Logical}}] }]
                          Actions: !If
                            - HasCertificate
                            - [{ Type: redirect, RedirectConfig: { Protocol: HTTPS, Port: "443", StatusCode: HTTP_301 } }]
                            - [{ Type: forward, TargetGroupArn: !Ref {{w.Logical}}TargetGroup }]
                      {{w.Logical}}HttpsRule:
                        Type: AWS::ElasticLoadBalancingV2::ListenerRule
                        Condition: HasCertificate
                        Properties:
                          ListenerArn: !Ref HttpsListener
                          Priority: {{priority}}
                          Conditions: [{ Field: host-header, Values: [!Ref Host{{w.Logical}}] }]
                          Actions: [{ Type: forward, TargetGroupArn: !Ref {{w.Logical}}TargetGroup }]
                    """);
                priority += 10;
            }
        }
        if (scheduled.Count > 0)
            sb.AppendLine("""
                  SchedulerRole:
                    Type: AWS::IAM::Role
                    Properties:
                      RoleName: !Sub "${AppName}-${Environment}-scheduler"
                      AssumeRolePolicyDocument:
                        Version: "2012-10-17"
                        Statement: [{ Effect: Allow, Principal: { Service: scheduler.amazonaws.com }, Action: sts:AssumeRole }]
                      Policies:
                        - PolicyName: run-task
                          PolicyDocument:
                            Version: "2012-10-17"
                            Statement:
                              - Effect: Allow
                                Action: [ecs:RunTask]
                                Resource: "*"
                                Condition: { ArnEquals: { "ecs:cluster": !GetAtt Cluster.Arn } }
                              - Effect: Allow
                                Action: [iam:PassRole]
                                Resource: [!GetAtt ExecutionRole.Arn, !GetAtt TaskRole.Arn]
                """);

        foreach (var d in containers)
        {
            var isWeb = d.Result.Project.Kind == ProjectKind.Web;
            var windows = d.Result.Hosting!.Primary == AwsHosting.EcsWindows;
            var secrets = SecretsFor(d);
            const string lifecycle = """'{"rules":[{"rulePriority":1,"description":"mantem 20 imagens","selection":{"tagStatus":"any","countType":"imageCountMoreThan","countNumber":20},"action":{"type":"expire"}}]}'""";
            var os = windows ? "WINDOWS_SERVER_2022_CORE" : "LINUX";
            sb.AppendLine($$"""
                  {{d.Logical}}Repository:
                    Type: AWS::ECR::Repository
                    Properties:
                      RepositoryName: !Sub "${AppName}/{{d.Slug}}"
                      ImageScanningConfiguration: { ScanOnPush: true }
                      LifecyclePolicy:
                        LifecyclePolicyText: {{lifecycle}}
                  {{d.Logical}}LogGroup:
                    Type: AWS::Logs::LogGroup
                    Properties:
                      LogGroupName: !Sub "/ecs/${AppName}-${Environment}/{{d.Slug}}"
                      RetentionInDays: 30
                  {{d.Logical}}TaskDefinition:
                    Type: AWS::ECS::TaskDefinition
                    Properties:
                      Family: !Sub "${AppName}-${Environment}-{{d.Slug}}"
                      RequiresCompatibilities: [FARGATE]
                      NetworkMode: awsvpc
                      Cpu: "{{(windows ? 1024 : 512)}}"
                      Memory: "{{(windows ? 2048 : 1024)}}"
                      RuntimePlatform: { OperatingSystemFamily: {{os}}, CpuArchitecture: X86_64 }
                      ExecutionRoleArn: !GetAtt ExecutionRole.Arn
                      TaskRoleArn: !GetAtt TaskRole.Arn
                      ContainerDefinitions:
                        - Name: {{d.Slug}}
                          Image: !Sub "${AWS::AccountId}.dkr.ecr.${AWS::Region}.amazonaws.com/${AppName}/{{d.Slug}}:${ImageTag}"
                          Essential: true
                          LogConfiguration:
                            LogDriver: awslogs
                            Options: { awslogs-group: !Ref {{d.Logical}}LogGroup, awslogs-region: !Ref "AWS::Region", awslogs-stream-prefix: ecs }
                          Environment:
                            - { Name: {{(isWeb ? "ASPNETCORE_ENVIRONMENT" : "DOTNET_ENVIRONMENT")}}, Value: Production }
                """);
            if (hasS3) sb.AppendLine("            - { Name: FILES_BUCKET, Value: !ImportValue { \"Fn::Sub\": \"${AppName}-${Environment}-FilesBucketName\" } }");
            if (workers.Contains(d)) sb.AppendLine($"            - {{ Name: QUEUE_URL, Value: !ImportValue {{ \"Fn::Sub\": \"${{AppName}}-${{Environment}}-{d.Logical}QueueUrl\" }} }}");
            if (secrets.Count > 0)
            {
                sb.AppendLine("          # Segredos criados por _secrets/<projeto>/create-secrets.sh; referenciados pelo nome (mesma região).");
                sb.AppendLine("          Secrets:");
                foreach (var s in secrets) sb.AppendLine($"            - {{ Name: {s.EnvironmentVariable}, ValueFrom: !Sub \"arn:aws:secretsmanager:${{AWS::Region}}:${{AWS::AccountId}}:secret:{s.SecretName}\" }}");
            }
            if (isWeb) sb.AppendLine("          PortMappings: [{ ContainerPort: 8080, Protocol: tcp }]");
            if (isWeb || workers.Contains(d))
            {
                sb.AppendLine($$"""
                      {{d.Logical}}Service:
                        Type: AWS::ECS::Service
                    {{(isWeb ? "    DependsOn: [HttpListener]" : "")}}
                        Properties:
                          ServiceName: {{d.Slug}}
                          Cluster: !Ref Cluster
                          LaunchType: FARGATE
                          TaskDefinition: !Ref {{d.Logical}}TaskDefinition
                          DesiredCount: {{(isWeb ? "!Ref WebDesiredCount" : "1")}}
                          EnableExecuteCommand: true
                          DeploymentConfiguration:
                            DeploymentCircuitBreaker: { Enable: true, Rollback: true }
                            MaximumPercent: 200
                            MinimumHealthyPercent: {{(isWeb ? 100 : 0)}}
                          NetworkConfiguration:
                            AwsvpcConfiguration:
                              Subnets: !Split [",", !ImportValue { "Fn::Sub": "${AppName}-${Environment}-PrivateSubnets" }]
                              SecurityGroups: [!Ref TaskSecurityGroup]
                              AssignPublicIp: DISABLED
                    """);
                if (isWeb)
                    sb.AppendLine($$"""
                              HealthCheckGracePeriodSeconds: 60
                              LoadBalancers:
                                - { ContainerName: {{d.Slug}}, ContainerPort: 8080, TargetGroupArn: !Ref {{d.Logical}}TargetGroup }
                        """);
                sb.AppendLine($$"""
                      {{d.Logical}}CpuAlarm:
                        Type: AWS::CloudWatch::Alarm
                        Properties:
                          AlarmName: !Sub "${AppName}-${Environment}-{{d.Slug}}-cpu"
                          Namespace: AWS/ECS
                          MetricName: CPUUtilization
                          Dimensions:
                            - { Name: ClusterName, Value: !Ref Cluster }
                            - { Name: ServiceName, Value: {{d.Slug}} }
                          Statistic: Average
                          Period: 300
                          EvaluationPeriods: 3
                          Threshold: 85
                          ComparisonOperator: GreaterThanThreshold
                          AlarmActions: [!Ref AlertsTopic]
                    """);
            }
            else
            {
                sb.AppendLine($$"""
                      {{d.Logical}}Schedule:
                        Type: AWS::Scheduler::Schedule
                        Properties:
                          Name: !Sub "${AppName}-${Environment}-{{d.Slug}}"
                          ScheduleExpression: !Ref Schedule{{d.Logical}}
                          FlexibleTimeWindow: { Mode: "OFF" }
                          Target:
                            Arn: !GetAtt Cluster.Arn
                            RoleArn: !GetAtt SchedulerRole.Arn
                            EcsParameters:
                              TaskDefinitionArn: !Ref {{d.Logical}}TaskDefinition
                              LaunchType: FARGATE
                              NetworkConfiguration:
                                AwsvpcConfiguration:
                                  Subnets: !Split [",", !ImportValue { "Fn::Sub": "${AppName}-${Environment}-PrivateSubnets" }]
                                  SecurityGroups: [!Ref TaskSecurityGroup]
                                  AssignPublicIp: DISABLED
                            RetryPolicy: { MaximumRetryAttempts: 2 }
                    """);
            }
        }

        sb.AppendLine();
        sb.AppendLine("Outputs:");
        sb.AppendLine("  ClusterName:");
        sb.AppendLine("    Value: !Ref Cluster");
        if (webs.Count > 0)
        {
            sb.AppendLine("  AlbDnsName:");
            sb.AppendLine("    Value: !GetAtt LoadBalancer.DNSName");
        }
        foreach (var d in containers)
        {
            sb.AppendLine($"  {d.Logical}Repository:");
            sb.AppendLine($"    Value: !GetAtt {d.Logical}Repository.RepositoryUri");
        }
        sb.AppendLine("  TaskSecurityGroupId:");
        sb.AppendLine("    Value: !Ref TaskSecurityGroup");
        sb.AppendLine("    Export: { Name: !Sub \"${AppName}-${Environment}-TaskSecurityGroupId\" }");
        return Yaml(sb);
    }

    private static string Lambda(SolutionResult result, List<Deployable> lambdas, bool hasDb, bool hasS3)
    {
        var mailbox = lambdas.Where(l => l.Profile.Has(Signal.MailboxReading)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine("""
            AWSTemplateFormatVersion: "2010-09-09"
            Description: "Funções Lambda (.NET) das automações orientadas a evento, com DLQ e gatilhos. Gerado pelo Migrator."

            Parameters:
              AppName:
                Type: String
              Environment:
                Type: String
                Default: prod
                AllowedValues: [dev, hml, prod]
              LambdaRuntime:
                Type: String
                Default: dotnet10
                Description: Runtime gerenciado do Lambda para .NET (ajuste se a região ainda não oferecer o .NET 10).
              PackageBucket:
                Type: String
                Description: Bucket com os pacotes (.zip de 'dotnet lambda package'); o workflow atualiza o código depois.
            """);
        foreach (var l in lambdas)
            sb.AppendLine($$"""
                  PackageKey{{l.Logical}}:
                    Type: String
                    Default: lambda/{{l.Slug}}.zip
                """);
        if (mailbox.Count > 0)
            sb.AppendLine("""
                  MailboxPollSchedule:
                    Type: String
                    Default: rate(5 minutes)
                    Description: Frequência com que a função consulta a caixa postal (Microsoft Graph/IMAP) enquanto o recebimento pelo SES não estiver configurado.
                  EnableSesInbound:
                    Type: String
                    Default: "false"
                    AllowedValues: ["true", "false"]
                    Description: Cria o recebimento de e-mail pelo SES (exige domínio verificado e MX apontando para o SES).
                  InboundRecipients:
                    Type: CommaDelimitedList
                    Default: ""
                    Description: "Endereços/domínios recebidos pelo SES (ex.: pedidos@exemplo.com.br)."
                """);
        if (mailbox.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Conditions:");
            sb.AppendLine("  SesInbound: !Equals [!Ref EnableSesInbound, \"true\"]");
        }
        sb.AppendLine();
        sb.AppendLine("""
            Resources:
              LambdaRole:
                Type: AWS::IAM::Role
                Properties:
                  RoleName: !Sub "${AppName}-${Environment}-lambda"
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
                            Resource: !Sub "arn:aws:secretsmanager:${AWS::Region}:${AWS::AccountId}:secret:${AppName}/*"
                          - Effect: Allow
                            Action: [sqs:SendMessage, sqs:ReceiveMessage, sqs:DeleteMessage, sqs:GetQueueAttributes, sqs:ChangeMessageVisibility]
                            Resource: !Sub "arn:aws:sqs:${AWS::Region}:${AWS::AccountId}:${AppName}-${Environment}-*"
            """);
        if (hasS3)
            sb.AppendLine("""
                              - Effect: Allow
                                Action: [s3:GetObject, s3:PutObject, s3:DeleteObject, s3:ListBucket]
                                Resource:
                                  - !ImportValue { "Fn::Sub": "${AppName}-${Environment}-FilesBucketArn" }
                                  - !Sub
                                    - "${BucketArn}/*"
                                    - BucketArn: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-FilesBucketArn" }
                """);
        sb.AppendLine("""
              LambdaSecurityGroup:
                Type: AWS::EC2::SecurityGroup
                Properties:
                  GroupDescription: Lambda (acesso ao RDS e aos endpoints da VPC)
                  VpcId: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-VpcId" }
            """);
        if (hasDb)
            sb.AppendLine("""
                  DbIngress:
                    Type: AWS::EC2::SecurityGroupIngress
                    Properties:
                      GroupId: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-DbSecurityGroupId" }
                      IpProtocol: tcp
                      FromPort: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-DbPort" }
                      ToPort: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-DbPort" }
                      SourceSecurityGroupId: !Ref LambdaSecurityGroup
                """);
        if (mailbox.Count > 0)
            sb.AppendLine("""
                  SchedulerRole:
                    Type: AWS::IAM::Role
                    Properties:
                      RoleName: !Sub "${AppName}-${Environment}-lambda-scheduler"
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
                                Resource: !Sub "arn:aws:lambda:${AWS::Region}:${AWS::AccountId}:function:${AppName}-${Environment}-*"
                """);
        foreach (var l in lambdas)
        {
            var ns = string.IsNullOrWhiteSpace(l.Result.Project.RootNamespace) ? l.Result.Project.Name : l.Result.Project.RootNamespace;
            var assembly = string.IsNullOrWhiteSpace(l.Result.Project.AssemblyName) ? l.Result.Project.Name : l.Result.Project.AssemblyName;
            var fileDriven = FileDriven(l.Profile);
            var handler = fileDriven ? "FunctionHandler" : "QueueHandler";
            var secrets = SecretsFor(l);
            sb.AppendLine($$"""
                  {{l.Logical}}Dlq:
                    Type: AWS::SQS::Queue
                    Properties:
                      QueueName: !Sub "${AppName}-${Environment}-{{l.Slug}}-dlq"
                  {{l.Logical}}LogGroup:
                    Type: AWS::Logs::LogGroup
                    Properties:
                      LogGroupName: !Sub "/aws/lambda/${AppName}-${Environment}-{{l.Slug}}"
                      RetentionInDays: 30
                  {{l.Logical}}Function:
                    Type: AWS::Lambda::Function
                    Properties:
                      FunctionName: !Sub "${AppName}-${Environment}-{{l.Slug}}"
                      Runtime: !Ref LambdaRuntime
                      Architectures: [arm64]
                      Handler: "{{assembly}}::{{ns}}.Function::{{handler}}"
                      Role: !GetAtt LambdaRole.Arn
                      Code: { S3Bucket: !Ref PackageBucket, S3Key: !Ref PackageKey{{l.Logical}} }
                      Timeout: 300
                      MemorySize: 512
                      DeadLetterConfig: { TargetArn: !GetAtt {{l.Logical}}Dlq.Arn }
                      VpcConfig:
                        SubnetIds: !Split [",", !ImportValue { "Fn::Sub": "${AppName}-${Environment}-PrivateSubnets" }]
                        SecurityGroupIds: [!Ref LambdaSecurityGroup]
                      Environment:
                        Variables:
                          DOTNET_ENVIRONMENT: Production
                """);
            if (hasS3) sb.AppendLine("          FILES_BUCKET: !ImportValue { \"Fn::Sub\": \"${AppName}-${Environment}-FilesBucketName\" }");
            foreach (var s in secrets) sb.AppendLine($"          {s.EnvironmentVariable}_SECRET: \"{s.SecretName}\" # leia com AWSSDK.SecretsManager (Lambda não injeta segredos como o ECS)");
            if (fileDriven && hasS3)
                sb.AppendLine($$"""
                      {{l.Logical}}FilesTrigger:
                        Type: AWS::Lambda::EventSourceMapping
                        Properties:
                          FunctionName: !Ref {{l.Logical}}Function
                          EventSourceArn: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-FilesEventsQueueArn" }
                          BatchSize: 1
                          FunctionResponseTypes: [ReportBatchItemFailures]
                    """);
            if (!fileDriven)
                sb.AppendLine($$"""
                      {{l.Logical}}Queue:
                        Type: AWS::SQS::Queue
                        Properties:
                          QueueName: !Sub "${AppName}-${Environment}-{{l.Slug}}"
                          VisibilityTimeout: 360
                          RedrivePolicy: { deadLetterTargetArn: !GetAtt {{l.Logical}}Dlq.Arn, maxReceiveCount: 5 }
                      {{l.Logical}}QueueTrigger:
                        Type: AWS::Lambda::EventSourceMapping
                        Properties:
                          FunctionName: !Ref {{l.Logical}}Function
                          EventSourceArn: !GetAtt {{l.Logical}}Queue.Arn
                          BatchSize: 10
                          FunctionResponseTypes: [ReportBatchItemFailures]
                    """);
            if (l.Profile.Has(Signal.MailboxReading))
                sb.AppendLine($$"""
                      # Enquanto a caixa continuar no Exchange/M365: a função é chamada a cada intervalo e consulta a caixa (Microsoft Graph/IMAP).
                      {{l.Logical}}MailboxSchedule:
                        Type: AWS::Scheduler::Schedule
                        Properties:
                          Name: !Sub "${AppName}-${Environment}-{{l.Slug}}-caixa-postal"
                          ScheduleExpression: !Ref MailboxPollSchedule
                          FlexibleTimeWindow: { Mode: "OFF" }
                          Target:
                            Arn: !GetAtt {{l.Logical}}Function.Arn
                            RoleArn: !GetAtt SchedulerRole.Arn
                            Input: '{"source":"scheduler","action":"poll-mailbox"}'
                      # E-mail recebido pelo SES vira objeto no bucket (prefixo {{l.Slug}}/entrada/email/) e o evento do S3 dispara a função.
                      {{l.Logical}}ReceiptRuleSet:
                        Type: AWS::SES::ReceiptRuleSet
                        Condition: SesInbound
                        Properties:
                          RuleSetName: !Sub "${AppName}-${Environment}-{{l.Slug}}"
                      {{l.Logical}}ReceiptRule:
                        Type: AWS::SES::ReceiptRule
                        Condition: SesInbound
                        Properties:
                          RuleSetName: !Ref {{l.Logical}}ReceiptRuleSet
                          Rule:
                            Name: {{l.Slug}}-para-s3
                            Enabled: true
                            ScanEnabled: true
                            Recipients: !Ref InboundRecipients
                            Actions:
                              - S3Action:
                                  BucketName: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-FilesBucketName" }
                                  ObjectKeyPrefix: {{l.Slug}}/entrada/email/
                    """);
            sb.AppendLine($$"""
                  {{l.Logical}}ErrorsAlarm:
                    Type: AWS::CloudWatch::Alarm
                    Properties:
                      AlarmName: !Sub "${AppName}-${Environment}-{{l.Slug}}-errors"
                      Namespace: AWS/Lambda
                      MetricName: Errors
                      Dimensions: [{ Name: FunctionName, Value: !Ref {{l.Logical}}Function }]
                      Statistic: Sum
                      Period: 300
                      EvaluationPeriods: 1
                      Threshold: 1
                      ComparisonOperator: GreaterThanOrEqualToThreshold
                      TreatMissingData: notBreaching
                """);
        }
        sb.AppendLine();
        sb.AppendLine("Outputs:");
        foreach (var l in lambdas)
        {
            sb.AppendLine($"  {l.Logical}FunctionName:");
            sb.AppendLine($"    Value: !Ref {l.Logical}Function");
        }
        return Yaml(sb);
    }
}
