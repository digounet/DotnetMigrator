using System.Text;
using Migrator.Core.Analysis;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>
/// .NET 10 target: the ECS <c>service.yml</c> as the platform writes it (seen in the portfolio repos): tag parameters,
/// roles and the shared load balancer from SSM parameters, one TCP listener per service on the shared NLB, Datadog agent
/// and FireLens sidecars, container health check, finops tags on task definition and service, target-tracking scaling with
/// the shared scaling role. The platform's pipeline pushes the image to <c>${FeatureName}-${MicroServiceName}-${Environment}</c>.
/// </summary>
public static partial class CloudFormationGenerator
{
    /// <summary>SSM parameters the platform publishes in every account (adjust in one place if the organization uses other names).</summary>
    private const string PlatformSsmCommon = "/Itau/Parameters/Common";
    private const string PlatformScalingRole = "/Shared/Role/ecs-scaling-role";
    private const string PlatformEcrAccount = "851725494844";
    private const string DatadogAgentImage = PlatformEcrAccount + ".dkr.ecr.sa-east-1.amazonaws.com/itau-corp-itau-ln6-container-datadog-agent-arm64:v0.0.1";
    private const string FluentBitImage = PlatformEcrAccount + ".dkr.ecr.sa-east-1.amazonaws.com/itau-corp-itau-ln6-container-fluent-bit-arm64:v0.0.4";

    /// <summary>Tags the platform requires on every resource (finops allocation, owners, repository).</summary>
    private const string TagParameters = """
          Squad:
            Description: TAG necessaria. Informar o nome da Squad. Verifique o arquivo de parametros
            Type: String
            Default: squad
          Finalidade:
            Description: TAG necessaria. Finalidade do projeto
            Type: String
            Default: Modernizacao
          Sigla:
            Description: TAG necessaria. Sigla do projeto
            Type: String
            Default: sigla
          Versao:
            Description: Versao da aplicacao para vinculo com o Datadog
            Type: String
            Default: 1.0.0
          TechTeamEmail:
            Description: TAG necessaria. E-mail do time responsavel
            Type: String
            Default: time@empresa.com.br
          OwnerTeamEmail:
            Description: TAG necessaria. E-mail do PO responsavel
            Type: String
            Default: po@empresa.com.br
          RepoUrl:
            Description: Url do projeto no Github
            Type: String
            Default: https://github.com/org/repo
          GithubRepoId:
            Description: ID numerico do repositorio GitHub para tag REPO_ID
            Type: String
            Default: "0"
        """;

    private static string FinopsTags(string indent) => $"""
        {indent}- Key: tech-team-email
        {indent}  Value: !Ref TechTeamEmail
        {indent}- Key: iu:finops:alocacao:projeto
        {indent}  Value: !Ref Projeto
        {indent}- Key: iu:finops:alocacao:squad
        {indent}  Value: !Ref Squad
        {indent}- Key: iu:finops:alocacao:sigla
        {indent}  Value: !Ref Sigla
        {indent}- Key: iu:finops:alocacao:servico-negocio
        {indent}  Value: !Ref Negocio
        {indent}- Key: owner-team-email
        {indent}  Value: !Ref OwnerTeamEmail
        {indent}- Key: squad
        {indent}  Value: !Ref Squad
        {indent}- Key: iu:finops:alocacao:produto
        {indent}  Value: !Ref NomeAplicacao
        {indent}- Key: finalidade
        {indent}  Value: !Ref Finalidade
        {indent}- Key: REPO_ID
        {indent}  Value: !Ref GithubRepoId
        {indent}- Key: repo-url
        {indent}  Value: !Ref RepoUrl
        """.Replace("\r\n", "\n");

    private static string ServiceTemplate(SolutionResult result, Deployable d, string micro, bool hasDb, bool hasS3, bool isWorker, HashSet<string> components)
    {
        var isWeb = d.Result.Project.Kind == ProjectKind.Web;
        var scheduled = d.Result.Hosting!.Primary == AwsHosting.EcsScheduledTask;
        var windows = d.Result.Hosting.Primary == AwsHosting.EcsWindows;
        var isService = isWeb || isWorker || d.Result.Hosting.Primary == AwsHosting.EcsFargateWorker;
        var settings = SettingsFor([d]);
        var secrets = SecretsFor(d);
        var sb = new StringBuilder();
        sb.AppendLine("AWSTemplateFormatVersion: 2010-09-09");
        sb.AppendLine($"Description: ECS {(isWeb ? "service" : scheduled ? "scheduled task" : "worker")} - {d.Result.Project.Name} ({result.SolutionName}), gerado pelo Migrator");
        sb.AppendLine();
        sb.AppendLine("Parameters:");
        if (isService)
            sb.AppendLine("""
                  DesiredNumberOfTasks:
                    Description: Number of tasks to launch for the service
                    Type: Number
                    Default: 1
                    MinValue: 1
                  MinCapacityTask:
                    Description: Numero minimo de tasks para o ECS
                    Type: "String"
                    Default: 1
                """);
        sb.AppendLine($$"""
              Projeto:
                Description: Descricao Projeto
                Type: "String"
                Default: {{result.SolutionName}}
              Negocio:
                Description: Descricao Oferta de Servico de Negocio
                Type: "String"
                Default: {{d.Result.Project.Name}} - {{(isWeb ? "aplicacao web" : "processamento em segundo plano")}} migrada para .NET 10
            """);
        if (isService)
            sb.AppendLine("""
                  MaxCapacityTask:
                    Description: Numero maximo de tasks para o ECS
                    Type: "String"
                    Default: 4
                """);
        sb.AppendLine($$"""
              ExposedPortInDockerfile:
                Description: Port defined in dockerfile and inside app
                Type: Number
                Default: 8080
            {{(isService ? "  ListenerContainerPort:\n    Description: Listening port for container\n    Type: Number\n    Default: 8080" : "")}}
              ContainerName:
                Description: Container Name
                Type: String
                Default: container
              ContainerCpu:
                Description: The number of cpu units reserved for the container.
                Type: Number
                Default: {{(windows ? 1024 : 256)}}
              ContainerMemory:
                Description: The amount (in MiB) of memory to present to the container.
                Type: Number
                Default: {{(windows ? 2048 : 512)}}
              ContainerMemoryReservation:
                Description: The soft limit (in MiB) of memory to reserve for the container.
                Type: Number
                Default: {{(windows ? 2048 : 512)}}
            """);
        sb.AppendLine(PipelineBlock);
        sb.AppendLine($$"""
              Environment:
                Type: String
                Default: dev
              EcsClusterName:
                Description: Name of the ECS Cluster
                Type: String
                Default: 'ecs-cluster-{{Feature(result)}}-fargate'
              VPCID:
                Description: ID da VPC
                Type:  String
              PrivateSubnetOne:
                Type:  String
              PrivateSubnetTwo:
                Type:  String
              PrivateSubnetThree:
                Type:  String
            """);
        if (isService)
            sb.AppendLine("""
                  CapacityProvider:
                    Type: String
                    Description: Provedor de capacidade que pode ser FARGATE ou FARGATE_SPOT
                    Default: FARGATE
                  WeightFargate:
                    Description: Numero minimo de maquinas quando estiver habilitado a funcao FargateSpot.
                    Type: Number
                    Default: 1
                """);
        sb.AppendLine(TagParameters);
        if (isService)
            sb.AppendLine($$"""
                  ScalingRoleArn:
                    Type: AWS::SSM::Parameter::Value<String>
                    Default: "{{PlatformScalingRole}}"
                  ScaleCpuTargetValue:
                    Description: Limite de utilizacao do CPU para aumentar ou diminuir as tarefas do ECS
                    Type: Number
                    Default: 50
                  ScaleCpuInCooldownPeriod:
                    Description: Periodo de espera para aumentar as tarefas do ECS
                    Type: Number
                    Default: 60
                  ScaleCpuOutCooldownPeriod:
                    Description: Periodo de espera para diminuir as tarefas do ECS
                    Type: Number
                    Default: 60
                """);
        sb.AppendLine($$"""
              NomeAplicacao:
                Description: Tag para identificar a aplicacao nos dashboards de custo da AWS e Cloudability
                Type: String
                Default: '{{Letters(result.SolutionName).ToUpperInvariant()}}: {{d.Result.Project.Name}}'
              CpuArchitecture:
                Description: ARM64 (padrao da plataforma, imagens linux/arm64) ou X86_64
                Type: String
                Default: {{(windows ? "X86_64" : "ARM64")}}
                AllowedValues: [ARM64, X86_64]
              EnableDatadog:
                Description: Sidecars datadog-agent e log_router (FireLens) com os logs enviados ao Datadog; "false" mantem awslogs no CloudWatch
                Type: String
                Default: "true"
                AllowedValues: ["true", "false"]
              DatadogAgentImage:
                Type: String
                Default: '{{DatadogAgentImage}}'
              FluentBitImage:
                Type: String
                Default: '{{FluentBitImage}}'
            """);
        if (scheduled)
            sb.AppendLine("""
                  ScheduleExpression:
                    Description: Expressao do EventBridge Scheduler (cron(...) ou rate(...)), em UTC
                    Type: String
                    Default: rate(15 minutes)
                """);
        sb.AppendLine("  #Config: valores que estavam fixos no codigo/appSettings, um por ambiente em <ambiente>/parameters.json");
        foreach (var (setting, parameter, _) in settings)
        {
            sb.AppendLine($"  {parameter}:");
            sb.AppendLine($"    Description: \"{(setting.Kind == SettingKind.Url ? "URL" : "E-mail")} {setting.Key} ({(setting.Source == SettingSource.Code ? "estava fixo no codigo" : "appSettings")})\"");
            sb.AppendLine("    Type: String");
            sb.AppendLine($"    Default: '{setting.Value.Replace("'", "''")}'");
        }
        sb.AppendLine();
        sb.AppendLine($$"""
              # A role needed by ECS.
              # "The ARN of the task execution role that containers in this task can assume. All containers in this task are granted the permissions that are specified in this role."
              # "There is an optional task execution IAM role that you can specify with Fargate to allow your Fargate tasks to make API calls to Amazon ECR."
              ExecutionRoleArn:
                Type: AWS::SSM::Parameter::Value<String>
                Default: '{{PlatformSsmCommon}}/ExecutionRoleArn'

              # "The Amazon Resource Name (ARN) of an AWS Identity and Access Management (IAM) role that grants containers in the task permission to call AWS APIs on your behalf."
              TaskExecutionRoleArn:
                Type: AWS::SSM::Parameter::Value<String>
                Default: '{{PlatformSsmCommon}}/TaskExecutionRoleArn'
            """);
        if (isWeb)
            sb.AppendLine($$"""

                  LoadBalancerArn:
                    Type: AWS::SSM::Parameter::Value<String>
                    Default: '{{PlatformSsmCommon}}/LoadBalancerArn'
                """);
        sb.AppendLine();
        sb.AppendLine("Conditions:");
        sb.AppendLine("  UseDatadog: !Equals [!Ref EnableDatadog, \"true\"]");
        sb.AppendLine();
        sb.AppendLine($$"""
            Resources:
              CloudWatchLogGroup:
                Type: AWS::Logs::LogGroup
                Properties:
                  LogGroupName: !Sub "${FeatureName}-${MicroServiceName}"
                  RetentionInDays: 30

              ErrorMetricFilter:
                Type: AWS::Logs::MetricFilter
                Properties:
                  LogGroupName: !Sub "${FeatureName}-${MicroServiceName}"
                  FilterPattern: "?ERROR ?Error ?Exception ?fail"
                  MetricTransformations:
                  - MetricValue: '1'
                    MetricNamespace: !Sub "${FeatureName}-${MicroServiceName}/errors"
                    MetricName: !Sub "${FeatureName}-${MicroServiceName}/ErrorCount"
                DependsOn: CloudWatchLogGroup

              ErrorAlarm:
                Type: AWS::CloudWatch::Alarm
                Properties:
                  AlarmName: !Sub "Alarm-${FeatureName}-${MicroServiceName}-errors"
                  AlarmDescription: "Erros no log da aplicacao (ajuste o FilterPattern ao formato de log)"
                  MetricName: !Sub "${FeatureName}-${MicroServiceName}/ErrorCount"
                  Namespace: !Sub "${FeatureName}-${MicroServiceName}/errors"
                  Statistic: Sum
                  Period: '60'
                  EvaluationPeriods: '1'
                  Threshold: '10'
                  AlarmActions:
                    - '{{SnsTopic}}'
                  OKActions:
                    - '{{SnsTopic}}'
                  ComparisonOperator: GreaterThanThreshold
                  TreatMissingData: notBreaching
                DependsOn: ErrorMetricFilter

              SecurityGroup:
                Type: 'AWS::EC2::SecurityGroup'
                Properties:
                  GroupDescription: !Ref EcsClusterName
                  VpcId: !Ref VPCID
                  SecurityGroupIngress:
                    - IpProtocol: tcp
                      FromPort: !Ref ExposedPortInDockerfile
                      ToPort: !Ref ExposedPortInDockerfile
                      CidrIp: 0.0.0.0/0
                  SecurityGroupEgress:
                    - CidrIp: 0.0.0.0/0
                      IpProtocol: "-1"
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

              ECSTaskDefinition:
                Type: "AWS::ECS::TaskDefinition"
                UpdateReplacePolicy: Retain
                DeletionPolicy: Retain
                Properties:
                  Family: !Sub "family-${FeatureName}-${MicroServiceName}"
                  NetworkMode: awsvpc
                  RequiresCompatibilities:
                    - FARGATE
                  RuntimePlatform:
                    CpuArchitecture: !Ref CpuArchitecture
                    OperatingSystemFamily: {{(windows ? "WINDOWS_SERVER_2022_CORE" : "LINUX")}}
                  Cpu: !Ref ContainerCpu
                  Memory: !Ref ContainerMemory
                  ExecutionRoleArn: !Ref ExecutionRoleArn
                  TaskRoleArn: !Ref TaskExecutionRoleArn
                  ContainerDefinitions:
                    - !If
                      - UseDatadog
                      - Name: datadog-agent
                        Image: !Ref DatadogAgentImage
                        Essential: false
                        PortMappings:
                          - ContainerPort: 8126
                            Protocol: tcp
                            HostPort: 8126
                          - ContainerPort: 8125
                            Protocol: udp
                            HostPort: 8125
                        Environment:
                          - Name: DD_TAGS
                            Value: !Sub "env:${Environment} sigla:${Sigla} cloud_provider:aws condominio:devops produto:${Projeto} jornada:${Negocio}"
                          - Name: DD_APM_ENABLED
                            Value: true
                          - Name: ECS_FARGATE
                            Value: true
                          - Name: DD_API_KEY
                            Value: !Sub org-itau-${Environment}
                          - Name: DD_SITE
                            Value: !Sub proxy.datadog.${Environment}.aws.cloud.ihf
                          - Name: DD_DD_URL
                            Value: !Sub https://proxy.datadog.${Environment}.aws.cloud.ihf:3834
                          - Name: DD_ORCHESTRATOR_EXPLORER_ORCHESTRATOR_DD_URL
                            Value: !Sub https://orchestrator.proxy.datadog.${Environment}.aws.cloud.ihf
                          - Name: DD_APM_DD_URL
                            Value: !Sub https://proxy.datadog.${Environment}.aws.cloud.ihf:3835
                          - Name: DD_APM_PROFILING_DD_URL
                            Value: !Sub https://proxy.datadog.${Environment}.aws.cloud.ihf:3836/api/v2/profile
                          - Name: DD_APM_TELEMETRY_DD_URL
                            Value: !Sub https://proxy.datadog.${Environment}.aws.cloud.ihf:3843
                          - Name: DD_PROCESS_CONFIG_PROCESS_DD_URL
                            Value: !Sub https://proxy.datadog.${Environment}.aws.cloud.ihf:3837
                          - Name: DD_LOGS_CONFIG_LOGS_DD_URL
                            Value: !Sub https://logs-proxy.datadog.${Environment}.aws.cloud.ihf:3838
                          - Name: DD_DATABASE_MONITORING_METRICS_LOGS_DD_URL
                            Value: !Sub https://proxy.datadog.${Environment}.aws.cloud.ihf:3839
                          - Name: DD_DATABASE_MONITORING_ACTIVITY_LOGS_DD_URL
                            Value: !Sub https://proxy.datadog.${Environment}.aws.cloud.ihf:3839
                          - Name: DD_DATABASE_MONITORING_SAMPLES_LOGS_DD_URL
                            Value: !Sub https://proxy.datadog.${Environment}.aws.cloud.ihf:3839
                          - Name: DD_NETWORK_DEVICES_METADATA_LOGS_DD_URL
                            Value: !Sub https://proxy.datadog.${Environment}.aws.cloud.ihf:3841
                          - Name: DD_NETWORK_DEVICES_SNMP_TRAPS_FORWARDER_LOGS_DD_URL
                            Value: !Sub https://proxy.datadog.${Environment}.aws.cloud.ihf:3842
                          - Name: DD_NETWORK_DEVICES_NETFLOW_FORWARDER_LOGS_DD_URL
                            Value: !Sub https://proxy.datadog.${Environment}.aws.cloud.ihf:3845
                          - Name: DD_REMOTE_CONFIGURATION_RC_DD_URL
                            Value: !Sub https://proxy.datadog.${Environment}.aws.cloud.ihf:3846
                          - Name: DD_DOGSTATSD_ORIGIN_DETECTION_CLIENT
                            Value: true
                          - Name: DD_DOGSTATSD_NON_LOCAL_TRAFFIC
                            Value: true
                          - Name: DD_APM_IGNORE_RESOURCES
                            Value: "GET /health, GET /metrics"
                        DockerLabels:
                          "com.datadoghq.tags.env": !Ref Environment
                          "com.datadoghq.tags.service": !Sub "${Sigla}-${FeatureName}-${MicroServiceName}"
                          "com.datadoghq.tags.version": !Ref Versao
                      - !Ref AWS::NoValue
                    - !If
                      - UseDatadog
                      - Name: log_router
                        Image: !Ref FluentBitImage
                        Essential: false
                        FirelensConfiguration:
                          Type: fluentbit
                          Options:
                            enable-ecs-log-metadata: true
                      - !Ref AWS::NoValue
                    - Name: !Sub ${ContainerName}
                      ##### NAO ALTERE O PARAMETRO "IMAGE" ABAIXO, IMPOSSIBILITA O DEPLOY DA PIPELINE #####
                      Image: !Sub '${DevToolsAccount}.dkr.ecr.${AWS::Region}.amazonaws.com/${FeatureName}-${MicroServiceName}-${Environment}'
                      PortMappings:
                        - ContainerPort: !Ref ExposedPortInDockerfile
                      Cpu: !Ref ContainerCpu
                      Memory: !Ref ContainerMemory
                      MemoryReservation: !Ref ContainerMemoryReservation
                      Essential: true
            """);
        if (isWeb)
            sb.AppendLine("""
                          HealthCheck:
                            Retries: 3
                            Command: [ "CMD-SHELL", !Sub "curl -f http://localhost:${ExposedPortInDockerfile}/health || exit 1" ]
                            Timeout: 5
                            Interval: 10
                            StartPeriod: 60
                """);
        sb.AppendLine($$"""
                      Environment:
                        - Name: "{{(isWeb ? "ASPNETCORE_ENVIRONMENT" : "DOTNET_ENVIRONMENT")}}"
                          Value: Production
                        - Name: "ambiente"
                          Value: !Ref Environment
            """);
        if (hasS3) sb.AppendLine("            - Name: FILES_BUCKET\n              Value: !ImportValue { \"Fn::Sub\": \"${FeatureName}-${Environment}-FilesBucketName\" }");
        if (isWorker) sb.AppendLine($"            - Name: QUEUE_URL\n              Value: !ImportValue {{ \"Fn::Sub\": \"${{FeatureName}}-${{Environment}}-{d.Logical}QueueUrl\" }}");
        foreach (var (setting, parameter, _) in settings)
            sb.AppendLine($"            - Name: \"{setting.EnvironmentVariable}\" # {setting.Key}\n              Value: !Ref {parameter}");
        sb.AppendLine("""
                        - Name: DD_ENV
                          Value: !Ref Environment
                        - Name: DD_SERVICE
                          Value: !Sub "${Sigla}-${FeatureName}-${MicroServiceName}"
                        - Name: DD_TAGS
                          Value: !Sub "env:${Environment} sigla:${Sigla} cloud_provider:aws condominio:devops produto:${Projeto} jornada:${Negocio}"
                        - Name: DD_LOGS_INJECTION
                          Value: true
                        - Name: DD_VERSION
                          Value: !Ref Versao
                        - Name: DD_TRACE_HEADER_TAGS
                          Value: x-correlationID:CorrelationId
            """);
        if (secrets.Count > 0)
        {
            sb.AppendLine("          # Segredos pelo nome no Secrets Manager (criados em data.yml; valores por _secrets/<projeto>/create-secrets.sh). Nunca no template.");
            sb.AppendLine("          Secrets:");
            foreach (var s in secrets)
                sb.AppendLine($"            - Name: {s.EnvironmentVariable}\n              ValueFrom: !Sub \"arn:aws:secretsmanager:${{AWS::Region}}:${{AWS::AccountId}}:secret:{s.SecretName}\"");
        }
        sb.AppendLine("""
                      LogConfiguration: !If
                        - UseDatadog
                        - LogDriver: awsfirelens
                          Options:
                            Name: "datadog"
                            apikey: !Sub "org-itau-${Environment}"
                            Host: !Sub "logs-proxy.datadog.${Environment}.aws.cloud.ihf"
                            port: 3838
                            dd_service: !Sub "${Sigla}-${FeatureName}-${MicroServiceName}"
                            dd_source: "dotnet"
                            dd_tags: !Sub "env:${Environment}, sigla:${Sigla}, cloud_provider:aws, condominio:devops, produto:${Projeto}, jornada:${Negocio}"
                            tls: "on"
                            tls.ca_file: "/etc/pki/ca-trust/source/anchors/ca_bundle.crt"
                            provider: "ecs"
                        - LogDriver: awslogs
                          Options:
                            awslogs-group: !Ref CloudWatchLogGroup
                            awslogs-region: !Ref "AWS::Region"
                            awslogs-stream-prefix: !Ref ContainerName
                      DockerLabels:
                        PROMETHEUS_TARGET: true
                        PROMETHEUS_EXPORTER_PORT: !Ref ExposedPortInDockerfile
                        PROMETHEUS_EXPORTER_PATH: "/metrics"
                        PROMETHEUS_EXPORTER_JOB_NAME: !Ref Negocio
                        promed.custom_labels: !Sub "Jornada=${Squad}"
                        "com.datadoghq.tags.env": !Ref Environment
                        "com.datadoghq.tags.service": !Sub "${Sigla}-${FeatureName}-${MicroServiceName}"
                        "com.datadoghq.tags.version": !Ref Versao
                  Tags:
            """);
        sb.AppendLine(FinopsTags("        "));
        if (isService)
        {
            sb.AppendLine("""

                  ECSService:
                    Type: AWS::ECS::Service
                    Properties:
                      ServiceName: !Sub service-${FeatureName}-${MicroServiceName}
                      Cluster: !Ref EcsClusterName
                      PropagateTags: SERVICE
                      DeploymentConfiguration:
                        MinimumHealthyPercent: 100
                        MaximumPercent: 200
                        DeploymentCircuitBreaker:
                          Enable: true
                          Rollback: true
                      TaskDefinition: !Ref 'ECSTaskDefinition'
                      DesiredCount: !Ref DesiredNumberOfTasks
                      EnableExecuteCommand: true
                      CapacityProviderStrategy:
                        - CapacityProvider: !Ref CapacityProvider
                          Weight: !Ref WeightFargate
                """);
            if (isWeb)
                sb.AppendLine("""
                          HealthCheckGracePeriodSeconds: 60
                          LoadBalancers:
                            - ContainerName: !Sub ${ContainerName}
                              ContainerPort: !Ref ExposedPortInDockerfile
                              TargetGroupArn: !Ref TargetGroup
                    """);
            sb.AppendLine("""
                      NetworkConfiguration:
                        AwsvpcConfiguration:
                          AssignPublicIp: DISABLED
                          Subnets:
                            - !Ref PrivateSubnetOne
                            - !Ref PrivateSubnetTwo
                            - !Ref PrivateSubnetThree
                          SecurityGroups:
                            - !Ref SecurityGroup
                      SchedulingStrategy: REPLICA
                      Tags:
                """);
            sb.AppendLine(FinopsTags("        "));
            if (isWeb)
                sb.AppendLine("""
                        DependsOn: Listener

                      TargetGroup:
                        Type: AWS::ElasticLoadBalancingV2::TargetGroup
                        Properties:
                          Port: !Ref ListenerContainerPort
                          Protocol: TCP
                          VpcId: !Ref VPCID
                          TargetType: ip
                          HealthCheckProtocol: HTTP
                          HealthCheckPath: /health
                          TargetGroupAttributes:
                            - Key: deregistration_delay.timeout_seconds
                              Value: '5'

                      # Um listener TCP por servico no Network Load Balancer compartilhado (porta = ListenerContainerPort).
                      Listener:
                        Type: AWS::ElasticLoadBalancingV2::Listener
                        Properties:
                          DefaultActions:
                            - Type: forward
                              TargetGroupArn: !Ref TargetGroup
                          LoadBalancerArn: !Ref LoadBalancerArn
                          Port: !Ref ListenerContainerPort
                          Protocol: TCP
                    """);
            sb.AppendLine("""

                  ScalableTarget:
                    Type: "AWS::ApplicationAutoScaling::ScalableTarget"
                    Properties:
                      MinCapacity: !Ref MinCapacityTask
                      MaxCapacity: !Ref MaxCapacityTask
                      ResourceId: !Join
                        - "/"
                        - - service
                          - !Ref EcsClusterName
                          - !GetAtt ECSService.Name
                      RoleARN: !Ref ScalingRoleArn
                      ScalableDimension: ecs:service:DesiredCount
                      ServiceNamespace: ecs

                  ScalingPolicy:
                    Type: "AWS::ApplicationAutoScaling::ScalingPolicy"
                    Properties:
                      PolicyName: ScaleWithCpu
                      PolicyType: TargetTrackingScaling
                      ScalingTargetId: !Ref ScalableTarget
                      TargetTrackingScalingPolicyConfiguration:
                        PredefinedMetricSpecification:
                          PredefinedMetricType: ECSServiceAverageCPUUtilization
                        TargetValue: !Ref ScaleCpuTargetValue
                        ScaleInCooldown: !Ref ScaleCpuInCooldownPeriod
                        ScaleOutCooldown: !Ref ScaleCpuOutCooldownPeriod
                """);
        }
        if (scheduled)
            sb.AppendLine("""

                  SchedulerRole:
                    Type: AWS::IAM::Role
                    Properties:
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
                                Condition: { ArnEquals: { "ecs:cluster": !Sub "arn:aws:ecs:${AWS::Region}:${AWS::AccountId}:cluster/${EcsClusterName}" } }
                              - Effect: Allow
                                Action: [iam:PassRole]
                                Resource: [!Ref ExecutionRoleArn, !Ref TaskExecutionRoleArn]

                  Schedule:
                    Type: AWS::Scheduler::Schedule
                    Properties:
                      Name: !Sub "${FeatureName}-${Environment}-${MicroServiceName}"
                      ScheduleExpression: !Ref ScheduleExpression
                      FlexibleTimeWindow: { Mode: "OFF" }
                      Target:
                        Arn: !Sub "arn:aws:ecs:${AWS::Region}:${AWS::AccountId}:cluster/${EcsClusterName}"
                        RoleArn: !GetAtt SchedulerRole.Arn
                        EcsParameters:
                          TaskDefinitionArn: !Ref ECSTaskDefinition
                          LaunchType: FARGATE
                          NetworkConfiguration:
                            AwsvpcConfiguration:
                              AssignPublicIp: DISABLED
                              SecurityGroups: [!Ref SecurityGroup]
                              Subnets: [!Ref PrivateSubnetOne, !Ref PrivateSubnetTwo, !Ref PrivateSubnetThree]
                        RetryPolicy: { MaximumRetryAttempts: 2 }
                """);
        sb.AppendLine();
        sb.AppendLine("Outputs:");
        sb.AppendLine("  TaskDefinitionArn:");
        sb.AppendLine("    Value: !Ref ECSTaskDefinition");
        sb.AppendLine("  LogGroupName:");
        sb.AppendLine("    Value: !Ref CloudWatchLogGroup");
        if (isService)
        {
            sb.AppendLine("  ServiceName:");
            sb.AppendLine("    Value: !GetAtt ECSService.Name");
        }
        return Yaml(sb);
    }

    private static List<(string Key, string Value)> ServiceParameters(SolutionResult result, Deployable d, string environment, string feature, bool isWorker)
    {
        var prod = environment == "prod";
        var isWeb = d.Result.Project.Kind == ProjectKind.Web;
        var isService = isWeb || isWorker || d.Result.Hosting!.Primary == AwsHosting.EcsFargateWorker;
        var list = new List<(string, string)>
        {
            ("VPCID", "vpc-xxxxxxxxxxxxxxxxx"), ("PrivateSubnetOne", "subnet-xxxxxxxxxxxxxxxx1"), ("PrivateSubnetTwo", "subnet-xxxxxxxxxxxxxxxx2"), ("PrivateSubnetThree", "subnet-xxxxxxxxxxxxxxxx3")
        };
        if (isService)
        {
            list.Add(("DesiredNumberOfTasks", prod && isWeb ? "2" : "1"));
            list.Add(("MinCapacityTask", prod && isWeb ? "2" : "1"));
            list.Add(("MaxCapacityTask", prod ? "6" : "2"));
        }
        list.Add(("ExposedPortInDockerfile", "8080"));
        if (isService) list.Add(("ListenerContainerPort", "8080")); // uma porta por servico no NLB compartilhado: ajuste para nao colidir com outros servicos
        list.Add(("ContainerCpu", prod ? "512" : "256"));
        list.Add(("ContainerMemory", prod ? "1024" : "512"));
        list.Add(("ContainerMemoryReservation", prod ? "1024" : "512"));
        if (isService)
        {
            list.Add(("CapacityProvider", prod ? "FARGATE" : "FARGATE_SPOT"));
            list.Add(("WeightFargate", "1"));
        }
        list.Add(("Environment", environment));
        list.Add(("FeatureName", feature));
        list.Add(("MicroServiceName", Micro(result, d)));
        list.Add(("DevToolsAccount", "123456789012"));
        list.Add(("EcsClusterName", $"ecs-cluster-{feature}-fargate"));
        list.Add(("Squad", "squad"));
        list.Add(("Sigla", "sigla"));
        list.Add(("Versao", "1.0.0"));
        list.Add(("TechTeamEmail", "time@empresa.com.br"));
        list.Add(("OwnerTeamEmail", "po@empresa.com.br"));
        list.Add(("GithubRepoId", "0"));
        if (d.Result.Hosting!.Primary == AwsHosting.EcsScheduledTask) list.Add(("ScheduleExpression", "rate(15 minutes)"));
        foreach (var (setting, parameter, _) in SettingsFor([d])) list.Add((parameter, SettingValue(setting, environment)));
        return list;
    }
}
