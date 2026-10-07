using System.Text;
using Migrator.Core.Analysis;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>
/// .NET Framework target (the default): EC2 Windows lift-and-shift in the platform's "one service per template" layout.
/// <c>service.yml</c> = launch template (user data installs IIS/.NET 4.8.1/CodeDeploy/CloudWatch agents), Auto Scaling group,
/// target group + rule on the shared ALB listener, CodeDeploy application/deployment group, Parameter Store entries for the
/// URLs/e-mails, log group and alarms. VPC, subnets, roles and the listener are parameters, like the ECS template.
/// </summary>
public static partial class CloudFormationGenerator
{
    private static string Ec2ServiceTemplate(SolutionResult result, Deployable d, string micro, bool hasDb, bool hasS3, bool hasFsx, HashSet<string> components)
    {
        var isWeb = d.Result.Project.Kind == ProjectKind.Web;
        var settings = SettingsFor([d]);
        var sb = new StringBuilder();
        sb.AppendLine("AWSTemplateFormatVersion: \"2010-09-09\"");
        sb.AppendLine($"Description: \"EC2 Windows {(isWeb ? "(IIS)" : d.Result.Project.Kind == ProjectKind.WindowsService ? "(Windows Service)" : "(tarefa agendada)")} {d.Result.Project.Name} ({result.SolutionName}), .NET Framework 4.8.1 sem alteração de código. Gerado pelo Migrator; VPC, subnets, roles e ALB vêm da infraestrutura compartilhada (parâmetros).\"");
        sb.AppendLine();
        sb.AppendLine("Parameters:");
        sb.AppendLine($$"""
              Projeto:
                Description: Descricao do projeto
                Type: String
                Default: "{{result.SolutionName}}"
              Negocio:
                Description: Descricao da oferta de servico de negocio
                Type: String
                Default: "{{(isWeb ? "Aplicação web" : "Processamento em segundo plano")}} {{d.Result.Project.Name}} (lift-and-shift .NET Framework)"
            """);
        sb.AppendLine(PipelineBlock);
        sb.AppendLine($$"""
              Environment:
                Type: String
                Default: dev
                AllowedValues: [dev, hom, prod]
              VPCID:
                Description: ID da VPC
                Type: String
              PrivateSubnetOne:
                Type: String
              PrivateSubnetTwo:
                Type: String
              PrivateSubnetThree:
                Type: String
              InstanceType:
                Type: String
                Default: t3.medium
              LatestAmiId:
                Type: AWS::SSM::Parameter::Value<AWS::EC2::Image::Id>
                Default: /aws/service/ami-windows-latest/Windows_Server-2022-English-Full-Base
                Description: "AMI Windows mais recente publicada pela AWS; troque pela AMI da plataforma (EC2 Image Builder) quando houver."
              InstanceProfileArn:
                Description: Instance profile fornecido pela plataforma (SSM, CloudWatch agent, segredos e Parameter Store do prefixo da aplicação, bucket de artefatos). Vazio = criar nesta stack.
                Type: String
                Default: ""
              CodeDeployRoleArn:
                Description: Service role do CodeDeploy (AWSCodeDeployRole). Vazio = criar nesta stack.
                Type: String
                Default: ""
              DesiredCapacity:
                Type: Number
                Default: 1
              MinCapacity:
                Type: Number
                Default: 1
              MaxCapacity:
                Type: Number
                Default: {{(isWeb ? 4 : 1)}}
              TimeZone:
                Type: String
                Default: E. South America Standard Time
                Description: Fuso horário das instâncias (tzutil), para manter DateTime.Now como on-premises.
            """);
        if (isWeb)
            sb.AppendLine($$"""
                  InstancePort:
                    Description: Porta do site no IIS (target group).
                    Type: Number
                    Default: 80
                  LoadBalancerListenerArn:
                    Description: Listener (HTTPS) do Application Load Balancer compartilhado onde a regra da aplicação é criada.
                    Type: String
                  ListenerRulePriority:
                    Type: Number
                    Default: 100
                  ListenerRulePath:
                    Description: Caminho roteado para esta aplicação no ALB compartilhado ("/*" quando o host header já a identifica).
                    Type: String
                    Default: "/*"
                  ListenerRuleHost:
                    Description: Host header (opcional) além do caminho.
                    Type: String
                    Default: "{{d.Slug}}.exemplo.com.br"
                  HealthCheckPath:
                    Type: String
                    Default: /
                    Description: "Caminho que responde 200 sem autenticação (ex.: /health.aspx)."
                """);
        SettingsParameters(sb, settings, "gravados no Parameter Store /<feature>/<env>/... e aplicados ao appSettings da instância pelo after-install.ps1");
        sb.AppendLine();
        sb.AppendLine("Conditions:");
        sb.AppendLine("  CreateInstanceProfile: !Equals [!Ref InstanceProfileArn, \"\"]");
        sb.AppendLine("  CreateCodeDeployRole: !Equals [!Ref CodeDeployRoleArn, \"\"]");
        if (isWeb) sb.AppendLine("  HasHost: !Not [!Equals [!Ref ListenerRuleHost, \"\"]]");
        sb.AppendLine();
        sb.AppendLine("Resources:");
        sb.AppendLine($$"""
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
                    - MetricValue: "1"
                      MetricNamespace: !Sub "${FeatureName}-${MicroServiceName}/errors"
                      MetricName: !Sub "${FeatureName}-${MicroServiceName}/ErrorCount"
                DependsOn: CloudWatchLogGroup

              ErrorAlarm:
                Type: AWS::CloudWatch::Alarm
                Properties:
                  AlarmName: !Sub "Alarm-${FeatureName}-${MicroServiceName}-errors"
                  AlarmDescription: "Erros no Event Log/logs da aplicação (ajuste o FilterPattern ao formato de log)"
                  MetricName: !Sub "${FeatureName}-${MicroServiceName}/ErrorCount"
                  Namespace: !Sub "${FeatureName}-${MicroServiceName}/errors"
                  Statistic: Sum
                  Period: 300
                  EvaluationPeriods: 1
                  Threshold: 10
                  ComparisonOperator: GreaterThanThreshold
                  TreatMissingData: notBreaching
                  AlarmActions:
                    - "{{SnsTopic}}" # tópico SNS padrão da conta (ajuste ao da organização)
                DependsOn: ErrorMetricFilter

              # Instance profile: Session Manager, CloudWatch agent, segredos e Parameter Store do prefixo da aplicação, bucket de artefatos.
              InstanceRole:
                Type: AWS::IAM::Role
                Condition: CreateInstanceProfile
                Properties:
                  RoleName: !Sub "${FeatureName}-${Environment}-${MicroServiceName}-ec2"
                  AssumeRolePolicyDocument:
                    Version: "2012-10-17"
                    Statement: [{ Effect: Allow, Principal: { Service: ec2.amazonaws.com }, Action: sts:AssumeRole }]
                  ManagedPolicyArns:
                    - arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore
                    - arn:aws:iam::aws:policy/CloudWatchAgentServerPolicy
                  Policies:
                    - PolicyName: aplicacao
                      PolicyDocument:
                        Version: "2012-10-17"
                        Statement:
                          - Effect: Allow
                            Action: [secretsmanager:GetSecretValue, secretsmanager:DescribeSecret]
                            Resource: !Sub "arn:aws:secretsmanager:${AWS::Region}:${AWS::AccountId}:secret:{{Slug(result.SolutionName)}}/*"
                          - Effect: Allow
                            Action: [ssm:GetParameter, ssm:GetParameters, ssm:GetParametersByPath]
                            Resource: !Sub "arn:aws:ssm:${AWS::Region}:${AWS::AccountId}:parameter/${FeatureName}/${Environment}/*"
                          - Effect: Allow
                            Action: [s3:GetObject, s3:ListBucket]
                            Resource:
                              - !ImportValue { "Fn::Sub": "${FeatureName}-${Environment}-ArtifactsBucketArn" }
                              - !Sub
                                - "${BucketArn}/*"
                                - BucketArn: !ImportValue { "Fn::Sub": "${FeatureName}-${Environment}-ArtifactsBucketArn" }
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
        if (components.Contains("ses"))
            sb.AppendLine("""
                              - Effect: Allow
                                Action: [ses:SendEmail, ses:SendRawEmail]
                                Resource: "*"
                """);
        sb.AppendLine("""
              InstanceProfile:
                Type: AWS::IAM::InstanceProfile
                Condition: CreateInstanceProfile
                Properties:
                  Roles: [!Ref InstanceRole]
              CodeDeployRole:
                Type: AWS::IAM::Role
                Condition: CreateCodeDeployRole
                Properties:
                  RoleName: !Sub "${FeatureName}-${Environment}-${MicroServiceName}-codedeploy"
                  AssumeRolePolicyDocument:
                    Version: "2012-10-17"
                    Statement: [{ Effect: Allow, Principal: { Service: codedeploy.amazonaws.com }, Action: sts:AssumeRole }]
                  ManagedPolicyArns: [arn:aws:iam::aws:policy/service-role/AWSCodeDeployRole]

              SecurityGroup:
                Type: AWS::EC2::SecurityGroup
                Properties:
                  GroupDescription: !Sub "${FeatureName}-${MicroServiceName} (sem RDP; use o Session Manager)"
                  VpcId: !Ref VPCID
            """);
        if (isWeb)
            sb.AppendLine("""
                      SecurityGroupIngress:
                        - IpProtocol: tcp
                          FromPort: !Ref InstancePort
                          ToPort: !Ref InstancePort
                          CidrIp: 0.0.0.0/0 # restrinja ao security group do ALB compartilhado quando ele for conhecido
                """);
        sb.AppendLine("""
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
        if (settings.Count > 0) sb.AppendLine("  # Parameter Store: o after-install.ps1 do CodeDeploy lê /<feature>/<env>/ e grava cada valor no appSettings da instância.");
        foreach (var (setting, parameter, _) in settings)
            sb.AppendLine($$"""
                  {{parameter}}Parameter:
                    Type: AWS::SSM::Parameter
                    Properties:
                      Name: !Sub "/${FeatureName}/${Environment}/{{setting.ParameterPath}}"
                      Type: String
                      Value: !Ref {{parameter}}
                      Description: "{{setting.Key}}"
                """);
        sb.AppendLine("""

              LaunchTemplate:
                Type: AWS::EC2::LaunchTemplate
                Properties:
                  LaunchTemplateName: !Sub "${FeatureName}-${Environment}-${MicroServiceName}"
                  LaunchTemplateData:
                    ImageId: !Ref LatestAmiId
                    InstanceType: !Ref InstanceType
                    IamInstanceProfile:
                      Arn: !If [CreateInstanceProfile, !GetAtt InstanceProfile.Arn, !Ref InstanceProfileArn]
                    SecurityGroupIds: [!Ref SecurityGroup]
                    MetadataOptions: { HttpTokens: required, InstanceMetadataTags: enabled } # tags no IMDS: os scripts descobrem feature/ambiente
                    BlockDeviceMappings:
                      - DeviceName: /dev/sda1
                        Ebs: { VolumeSize: 60, VolumeType: gp3, Encrypted: true, DeleteOnTermination: true }
                    TagSpecifications:
                      - ResourceType: instance
                        Tags:
                          - { Key: Name, Value: !Sub "${FeatureName}-${Environment}-${MicroServiceName}" }
                          - { Key: Application, Value: !Ref FeatureName }
                          - { Key: Environment, Value: !Ref Environment }
                    UserData:
                      Fn::Base64: !Sub |
            """);
        foreach (var line in UserData(d, isWeb).Split('\n')) sb.AppendLine(line.Length == 0 ? "" : "            " + line);
        sb.AppendLine($$"""
              AutoScalingGroup:
                Type: AWS::AutoScaling::AutoScalingGroup
                UpdatePolicy:
                  AutoScalingRollingUpdate: { MinInstancesInService: {{(isWeb ? 1 : 0)}}, MaxBatchSize: 1, PauseTime: PT10M }
                Properties:
                  AutoScalingGroupName: !Sub "${FeatureName}-${Environment}-${MicroServiceName}"
                  VPCZoneIdentifier: [!Ref PrivateSubnetOne, !Ref PrivateSubnetTwo, !Ref PrivateSubnetThree]
                  LaunchTemplate:
                    LaunchTemplateId: !Ref LaunchTemplate
                    Version: !GetAtt LaunchTemplate.LatestVersionNumber
                  MinSize: !Ref MinCapacity
                  MaxSize: !Ref MaxCapacity
                  DesiredCapacity: !Ref DesiredCapacity
                  HealthCheckType: {{(isWeb ? "ELB" : "EC2")}}
                  HealthCheckGracePeriod: 900
            {{(isWeb ? "      TargetGroupARNs: [!Ref TargetGroup]" : "      # Serviço/console: uma instância; o grupo só recria a instância em falha (sem escala horizontal).")}}
                  Tags:
                    - { Key: Name, Value: !Sub "${FeatureName}-${Environment}-${MicroServiceName}", PropagateAtLaunch: true }
                    - { Key: Application, Value: !Ref FeatureName, PropagateAtLaunch: true }
                    - { Key: Environment, Value: !Ref Environment, PropagateAtLaunch: true }
                    - { Key: Projeto, Value: !Ref Projeto, PropagateAtLaunch: true }
                    - { Key: Negocio, Value: !Ref Negocio, PropagateAtLaunch: true }
                    - { Key: DevToolsAccount, Value: !Ref DevToolsAccount, PropagateAtLaunch: false }
            """);
        if (isWeb)
            sb.AppendLine($$"""

                  TargetGroup:
                    Type: AWS::ElasticLoadBalancingV2::TargetGroup
                    Properties:
                      Name: !Sub "${FeatureName}-${MicroServiceName}"
                      VpcId: !Ref VPCID
                      Port: !Ref InstancePort
                      Protocol: HTTP
                      TargetType: instance
                      HealthCheckPath: !Ref HealthCheckPath
                      HealthCheckIntervalSeconds: 30
                      HealthyThresholdCount: 2
                      UnhealthyThresholdCount: 3
                      Matcher: { HttpCode: 200-399 }
                      TargetGroupAttributes:
                        - { Key: deregistration_delay.timeout_seconds, Value: "30" }
                {{(d.Profile.Has(Signal.InProcSession) ? "        - { Key: stickiness.enabled, Value: \"true\" } # sessão InProc: mantém o usuário na mesma instância\n        - { Key: stickiness.type, Value: lb_cookie }" : "        - { Key: stickiness.enabled, Value: \"false\" }")}}

                  ListenerRule:
                    Type: AWS::ElasticLoadBalancingV2::ListenerRule
                    Properties:
                      ListenerArn: !Ref LoadBalancerListenerArn
                      Priority: !Ref ListenerRulePriority
                      Conditions: !If
                        - HasHost
                        - - Field: path-pattern
                            Values: [!Ref ListenerRulePath]
                          - Field: host-header
                            Values: [!Ref ListenerRuleHost]
                        - - Field: path-pattern
                            Values: [!Ref ListenerRulePath]
                      Actions:
                        - Type: forward
                          TargetGroupArn: !Ref TargetGroup

                  UnhealthyAlarm:
                    Type: AWS::CloudWatch::Alarm
                    Properties:
                      AlarmName: !Sub "Alarm-${FeatureName}-${MicroServiceName}-unhealthy"
                      Namespace: AWS/ApplicationELB
                      MetricName: UnHealthyHostCount
                      Dimensions:
                        - { Name: LoadBalancer, Value: !Select [1, !Split ["loadbalancer/", !Select [0, !Split ["/listener/", !Ref LoadBalancerListenerArn]]]] }
                        - { Name: TargetGroup, Value: !GetAtt TargetGroup.TargetGroupFullName }
                      Statistic: Maximum
                      Period: 60
                      EvaluationPeriods: 3
                      Threshold: 0
                      ComparisonOperator: GreaterThanThreshold
                      TreatMissingData: notBreaching
                      AlarmActions:
                        - "{{SnsTopic}}"

                  ScalingPolicy:
                    Type: AWS::AutoScaling::ScalingPolicy
                    Properties:
                      AutoScalingGroupName: !Ref AutoScalingGroup
                      PolicyType: TargetTrackingScaling
                      TargetTrackingConfiguration:
                        PredefinedMetricSpecification: { PredefinedMetricType: ASGAverageCPUUtilization }
                        TargetValue: 60
                """);
        sb.AppendLine($$"""

              CodeDeployApplication:
                Type: AWS::CodeDeploy::Application
                Properties:
                  ApplicationName: !Sub "${FeatureName}-${Environment}-${MicroServiceName}"
                  ComputePlatform: Server

              DeploymentGroup:
                Type: AWS::CodeDeploy::DeploymentGroup
                Properties:
                  ApplicationName: !Ref CodeDeployApplication
                  DeploymentGroupName: !Sub "${FeatureName}-${Environment}-${MicroServiceName}"
                  ServiceRoleArn: !If [CreateCodeDeployRole, !GetAtt CodeDeployRole.Arn, !Ref CodeDeployRoleArn]
                  AutoScalingGroups: [!Ref AutoScalingGroup]
                  DeploymentConfigName: CodeDeployDefault.OneAtATime
                  DeploymentStyle: { DeploymentType: IN_PLACE, DeploymentOption: {{(isWeb ? "WITH_TRAFFIC_CONTROL" : "WITHOUT_TRAFFIC_CONTROL")}} }
            {{(isWeb ? "      LoadBalancerInfo: { TargetGroupInfoList: [{ Name: !GetAtt TargetGroup.TargetGroupName }] }" : "")}}
                  AutoRollbackConfiguration: { Enabled: true, Events: [DEPLOYMENT_FAILURE] }

              CpuAlarm:
                Type: AWS::CloudWatch::Alarm
                Properties:
                  AlarmName: !Sub "Alarm-${FeatureName}-${MicroServiceName}-cpu"
                  Namespace: AWS/EC2
                  MetricName: CPUUtilization
                  Dimensions: [{ Name: AutoScalingGroupName, Value: !Ref AutoScalingGroup }]
                  Statistic: Average
                  Period: 300
                  EvaluationPeriods: 3
                  Threshold: 85
                  ComparisonOperator: GreaterThanThreshold
                  AlarmActions:
                    - "{{SnsTopic}}"

            Outputs:
              AutoScalingGroupName:
                Value: !Ref AutoScalingGroup
              CodeDeployApplicationName:
                Value: !Ref CodeDeployApplication
              DeploymentGroupName:
                Value: !Ref DeploymentGroup
              LogGroupName:
                Value: !Ref CloudWatchLogGroup
            """);
        return Yaml(sb);
    }

    private static List<(string Key, string Value)> Ec2Parameters(SolutionResult result, Deployable d, string environment, string feature)
    {
        var prod = environment == "prod";
        var isWeb = d.Result.Project.Kind == ProjectKind.Web;
        var list = new List<(string, string)>
        {
            ("FeatureName", feature), ("MicroServiceName", Micro(result, d)), ("DevToolsAccount", "123456789012"), ("Environment", environment),
            ("VPCID", "vpc-xxxxxxxxxxxxxxxxx"), ("PrivateSubnetOne", "subnet-xxxxxxxxxxxxxxxx1"), ("PrivateSubnetTwo", "subnet-xxxxxxxxxxxxxxxx2"), ("PrivateSubnetThree", "subnet-xxxxxxxxxxxxxxxx3"),
            ("InstanceType", environment switch { "dev" => "t3.small", "hom" => "t3.medium", _ => "t3.large" }),
            ("InstanceProfileArn", ""), ("CodeDeployRoleArn", ""),
            ("DesiredCapacity", prod && isWeb ? "2" : "1"), ("MinCapacity", prod && isWeb ? "2" : "1"), ("MaxCapacity", isWeb ? (prod ? "4" : "2") : "1"),
            ("TimeZone", "E. South America Standard Time")
        };
        if (isWeb)
        {
            list.Add(("InstancePort", "80"));
            list.Add(("LoadBalancerListenerArn", "arn:aws:elasticloadbalancing:sa-east-1:123456789012:listener/app/alb-compartilhado/xxxx/yyyy"));
            list.Add(("ListenerRulePriority", "100"));
            list.Add(("ListenerRulePath", "/*"));
            list.Add(("ListenerRuleHost", prod ? $"{d.Slug}.empresa.com.br" : $"{d.Slug}-{environment}.empresa.com.br"));
            list.Add(("HealthCheckPath", "/"));
        }
        foreach (var (setting, parameter, _) in SettingsFor([d])) list.Add((parameter, SettingValue(setting, environment)));
        return list;
    }

    /// <summary>PowerShell user data: IIS/.NET 4.8.1 (web), MSMQ when used, CodeDeploy and CloudWatch agents, time zone. `${...}` is reserved for Fn::Sub.</summary>
    private static string UserData(Deployable d, bool isWeb)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<powershell>");
        sb.AppendLine("$ErrorActionPreference = \"Stop\"");
        sb.AppendLine("Start-Transcript -Path C:\\userdata.log -Append");
        sb.AppendLine("tzutil /s \"${TimeZone}\"");
        if (isWeb)
        {
            var features = new List<string> { "Web-Server", "Web-Asp-Net45", "Web-Net-Ext45", "Web-ISAPI-Ext", "Web-ISAPI-Filter", "Web-Mgmt-Console", "Web-Http-Redirect", "Web-Http-Logging", "Web-Stat-Compression", "Web-Dyn-Compression" };
            if (d.Profile.Has(Signal.WindowsAuth)) features.Add("Web-Windows-Auth");
            if (d.Profile.HasAny(Signal.WcfHost, Signal.Asmx)) features.AddRange(["NET-WCF-HTTP-Activation45", "NET-WCF-TCP-Activation45"]);
            sb.AppendLine("# IIS + ASP.NET 4.x");
            sb.AppendLine($"Install-WindowsFeature {string.Join(",", features)} | Out-Null");
            sb.AppendLine("Remove-Website -Name \"Default Web Site\" -ErrorAction SilentlyContinue");
        }
        if (d.Profile.Has(Signal.Msmq)) sb.AppendLine("Install-WindowsFeature MSMQ-Server | Out-Null");
        sb.AppendLine("# .NET Framework 4.8.1 (Windows Server 2022 traz 4.8; o instalador offline é idempotente)");
        sb.AppendLine("$release = (Get-ItemProperty \"HKLM:\\SOFTWARE\\Microsoft\\NET Framework Setup\\NDP\\v4\\Full\").Release");
        sb.AppendLine("if ($release -lt 533320) {");
        sb.AppendLine("  Invoke-WebRequest \"https://go.microsoft.com/fwlink/?LinkId=2203304\" -OutFile C:\\ndp481.exe");
        sb.AppendLine("  Start-Process C:\\ndp481.exe -ArgumentList \"/q /norestart\" -Wait");
        sb.AppendLine("}");
        sb.AppendLine("# Agente do CodeDeploy");
        sb.AppendLine("Invoke-WebRequest \"https://aws-codedeploy-${AWS::Region}.s3.${AWS::Region}.amazonaws.com/latest/codedeploy-agent.msi\" -OutFile C:\\codedeploy-agent.msi");
        sb.AppendLine("Start-Process msiexec.exe -ArgumentList \"/i C:\\codedeploy-agent.msi /quiet /l C:\\codedeploy-install.log\" -Wait");
        sb.AppendLine("# Agente do CloudWatch: Event Log de aplicação e logs em arquivo");
        sb.AppendLine("Invoke-WebRequest \"https://amazoncloudwatch-agent.s3.amazonaws.com/windows/amd64/latest/amazon-cloudwatch-agent.msi\" -OutFile C:\\amazon-cloudwatch-agent.msi");
        sb.AppendLine("Start-Process msiexec.exe -ArgumentList \"/i C:\\amazon-cloudwatch-agent.msi /quiet\" -Wait");
        sb.AppendLine("$cw = @\"");
        sb.AppendLine("{ \"logs\": { \"logs_collected\": {");
        sb.AppendLine("  \"windows_events\": { \"collect_list\": [ { \"event_name\": \"Application\", \"event_levels\": [\"ERROR\",\"WARNING\",\"INFORMATION\"], \"log_group_name\": \"${FeatureName}-${MicroServiceName}\", \"log_stream_name\": \"{instance_id}/eventlog\" } ] },");
        sb.AppendLine($"  \"files\": {{ \"collect_list\": [ {{ \"file_path\": \"{(isWeb ? $"C:\\\\inetpub\\\\{d.Slug}\\\\logs\\\\**" : $"C:\\\\apps\\\\{d.Slug}\\\\logs\\\\**")}\", \"log_group_name\": \"${{FeatureName}}-${{MicroServiceName}}\", \"log_stream_name\": \"{{instance_id}}/app\" }} ] }}");
        sb.AppendLine("} } }");
        sb.AppendLine("\"@");
        sb.AppendLine("Set-Content -Path C:\\cloudwatch-agent.json -Value $cw -Encoding ascii");
        sb.AppendLine("& \"C:\\Program Files\\Amazon\\AmazonCloudWatchAgent\\amazon-cloudwatch-agent-ctl.ps1\" -a fetch-config -m ec2 -c file:C:\\cloudwatch-agent.json -s");
        if (d.Profile.Has(Signal.UncPaths))
            sb.AppendLine("# Pastas de rede: mapeie o compartilhamento do FSx (\\\\<FsxDnsName>\\share) com o mesmo nome usado pela aplicação, ou configure o File Gateway.");
        sb.AppendLine("Stop-Transcript");
        sb.AppendLine("</powershell>");
        sb.AppendLine("<persist>true</persist>");
        return sb.ToString().Replace("\r\n", "\n");
    }

    // ------------------------------------------------------------------ CodeDeploy bundle (appspec + PowerShell hooks)

    private static IEnumerable<(string Path, string Content)> CodeDeployBundle(string app, string feature, string micro, Deployable d)
    {
        var isWeb = d.Result.Project.Kind == ProjectKind.Web;
        var isService = d.Result.Project.Kind == ProjectKind.WindowsService;
        var assembly = string.IsNullOrEmpty(d.Result.Project.AssemblyName) ? d.Result.Project.Name : d.Result.Project.AssemblyName;
        var root = isWeb ? $"C:\\inetpub\\{d.Slug}" : $"C:\\apps\\{d.Slug}";
        var serviceName = isService ? ServiceName(d) : null;
        var configFile = isWeb ? "web.config" : $"{assembly}.exe.config";
        var secretId = $"{app}/{d.Slug}/config";
        var taskName = $"{feature}-{micro}";

        yield return ("appspec.yml", $"""
            # Gerado pelo Migrator. Pacote: appspec.yml + app/ (saída do publish) + scripts/.
            version: 0.0
            os: windows
            files:
              - source: /app
                destination: {root}
            file_exists_behavior: OVERWRITE
            hooks:
              BeforeInstall:
                - location: scripts/before-install.ps1
                  timeout: 300
              AfterInstall:
                - location: scripts/after-install.ps1
                  timeout: 600
              ApplicationStart:
                - location: scripts/application-start.ps1
                  timeout: 300
              ValidateService:
                - location: scripts/validate-service.ps1
                  timeout: 300

            """.Replace("\r\n", "\n"));

        var before = new StringBuilder("$ErrorActionPreference = \"Stop\"\n");
        if (isWeb) before.AppendLine($"Import-Module WebAdministration\nif (Test-Path \"IIS:\\Sites\\{d.Slug}\") {{ Stop-Website -Name \"{d.Slug}\" -ErrorAction SilentlyContinue }}\nif (Test-Path \"IIS:\\AppPools\\{d.Slug}\") {{ Stop-WebAppPool -Name \"{d.Slug}\" -ErrorAction SilentlyContinue }}");
        else if (isService) before.AppendLine($"if (Get-Service -Name \"{serviceName}\" -ErrorAction SilentlyContinue) {{ Stop-Service -Name \"{serviceName}\" -Force }}");
        else before.AppendLine($"schtasks /End /TN \"{taskName}\" 2>$null | Out-Null");
        yield return ("scripts/before-install.ps1", before.ToString().Replace("\r\n", "\n"));

        var after = new StringBuilder();
        after.AppendLine("$ErrorActionPreference = \"Stop\"");
        after.AppendLine($"$root = \"{root}\"");
        after.AppendLine($"$config = Join-Path $root \"{configFile}\"");
        after.AppendLine("New-Item -ItemType Directory -Path (Join-Path $root \"logs\") -Force | Out-Null");
        after.AppendLine("Import-Module AWSPowerShell -ErrorAction SilentlyContinue");
        after.AppendLine("# Feature/ambiente vêm das tags da instância (IMDSv2, InstanceMetadataTags habilitado no launch template).");
        after.AppendLine("$imdsToken = Invoke-RestMethod -Method Put -Uri http://169.254.169.254/latest/api/token -Headers @{ 'X-aws-ec2-metadata-token-ttl-seconds' = '300' }");
        after.AppendLine("function Get-InstanceTag([string]$name) { try { Invoke-RestMethod -Uri \"http://169.254.169.254/latest/meta-data/tags/instance/$name\" -Headers @{ 'X-aws-ec2-metadata-token' = $imdsToken } } catch { $null } }");
        after.AppendLine($"$feature = Get-InstanceTag 'Application'; if (-not $feature) {{ $feature = \"{feature}\" }}");
        after.AppendLine("$environment = Get-InstanceTag 'Environment'; if (-not $environment) { $environment = 'dev' }");
        after.AppendLine("$updates = @{}");
        after.AppendLine("# 1) Parameter Store /<feature>/<ambiente>/...: URLs e e-mails (um valor por ambiente, vindos dos parâmetros da stack).");
        after.AppendLine("try {");
        after.AppendLine("  foreach ($p in (Get-SSMParametersByPath -Path \"/$feature/$environment/\" -Recursive)) { $updates['AppSettings:' + ($p.Name.Substring(\"/$feature/$environment/\".Length) -replace '/', ':')] = $p.Value }");
        after.AppendLine("} catch { Write-Host \"Parameter Store indisponível: $_\" }");
        after.AppendLine("# 2) Segredo agregado <app>/<projeto>/config (JSON chave→valor) e segredos individuais criados por _secrets/create-secrets.sh.");
        after.AppendLine($"$secretId = \"{secretId}\"");
        after.AppendLine("try { $json = (Get-SECSecretValue -SecretId $secretId).SecretString; if ($json -and $json -ne 'PREENCHER') { foreach ($p in ($json | ConvertFrom-Json).PSObject.Properties) { $updates[$p.Name] = $p.Value } } } catch { Write-Host \"Segredo $secretId não encontrado\" }");
        after.AppendLine("$secretMap = @{");
        foreach (var s in SecretsFor(d)) after.AppendLine($"  '{s.ConfigPath}' = '{s.SecretName}'");
        after.AppendLine("}");
        after.AppendLine("foreach ($entry in $secretMap.GetEnumerator()) { try { $v = (Get-SECSecretValue -SecretId $entry.Value).SecretString; if ($v -and $v -ne 'PREENCHER') { $updates[$entry.Key] = $v } } catch { Write-Host \"Segredo $($entry.Value) não encontrado\" } }");
        after.AppendLine("# 3) Grava no config da instância; nada disso fica no repositório nem no pacote.");
        after.AppendLine("if ($updates.Count -gt 0 -and (Test-Path $config)) {");
        after.AppendLine("  [xml]$xml = Get-Content $config");
        after.AppendLine("  foreach ($p in $updates.GetEnumerator()) {");
        after.AppendLine("    if ($p.Key -like 'ConnectionStrings:*') {");
        after.AppendLine("      $node = $xml.SelectSingleNode(\"//connectionStrings/add[@name='\" + $p.Key.Substring(18) + \"']\")");
        after.AppendLine("      if ($node) { $node.SetAttribute('connectionString', $p.Value) }");
        after.AppendLine("    } else {");
        after.AppendLine("      $key = $p.Key -replace '^AppSettings:', ''");
        after.AppendLine("      $node = $xml.SelectSingleNode(\"//appSettings/add[@key='$key']\")");
        after.AppendLine("      if ($node) { $node.SetAttribute('value', $p.Value) }");
        after.AppendLine("    }");
        after.AppendLine("  }");
        after.AppendLine("  $xml.Save($config)");
        after.AppendLine("}");
        if (isWeb)
        {
            after.AppendLine("Import-Module WebAdministration");
            after.AppendLine($"if (-not (Test-Path \"IIS:\\AppPools\\{d.Slug}\")) {{ New-WebAppPool -Name \"{d.Slug}\" | Out-Null }}");
            after.AppendLine($"Set-ItemProperty \"IIS:\\AppPools\\{d.Slug}\" -Name managedRuntimeVersion -Value \"v4.0\"");
            after.AppendLine($"Set-ItemProperty \"IIS:\\AppPools\\{d.Slug}\" -Name managedPipelineMode -Value Integrated");
            after.AppendLine($"if (-not (Test-Path \"IIS:\\Sites\\{d.Slug}\")) {{ New-Website -Name \"{d.Slug}\" -PhysicalPath $root -ApplicationPool \"{d.Slug}\" -Port 80 | Out-Null }}");
            after.AppendLine($"Set-ItemProperty \"IIS:\\Sites\\{d.Slug}\" -Name physicalPath -Value $root");
            after.AppendLine("# Permissão de escrita para o app pool nas pastas que a aplicação grava (App_Data, logs)");
            after.AppendLine($"icacls $root /grant \"IIS AppPool\\{d.Slug}:(OI)(CI)M\" /T /Q | Out-Null");
        }
        else if (isService)
        {
            after.AppendLine($"$exe = Join-Path $root \"{assembly}.exe\"");
            after.AppendLine($"if (-not (Get-Service -Name \"{serviceName}\" -ErrorAction SilentlyContinue)) {{ New-Service -Name \"{serviceName}\" -BinaryPathName \"`\"$exe`\"\" -StartupType Automatic -DisplayName \"{serviceName}\" | Out-Null }}");
            after.AppendLine($"sc.exe failure \"{serviceName}\" reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null");
        }
        else
        {
            after.AppendLine($"$exe = Join-Path $root \"{assembly}.exe\"");
            after.AppendLine("# Ajuste o intervalo (/SC MINUTE /MO 15) conforme o agendamento que a aplicação tinha no Agendador de Tarefas on-premises.");
            after.AppendLine($"schtasks /Create /F /SC MINUTE /MO 15 /TN \"{taskName}\" /TR \"`\"$exe`\"\" /RU SYSTEM /RL HIGHEST | Out-Null");
        }
        yield return ("scripts/after-install.ps1", after.ToString().Replace("\r\n", "\n"));

        var start = isWeb ? $"$ErrorActionPreference = \"Stop\"\nImport-Module WebAdministration\nStart-WebAppPool -Name \"{d.Slug}\"\nStart-Website -Name \"{d.Slug}\"\n"
            : isService ? $"$ErrorActionPreference = \"Stop\"\nStart-Service -Name \"{serviceName}\"\n"
            : $"# Tarefa agendada: o Agendador dispara no próximo intervalo. Execução imediata opcional:\nschtasks /Run /TN \"{taskName}\" | Out-Null\n";
        yield return ("scripts/application-start.ps1", start);

        var validate = isWeb
            ? "$ErrorActionPreference = \"Stop\"\n$path = \"/\" # mesmo caminho do parâmetro HealthCheckPath da stack\nfor ($i = 0; $i -lt 10; $i++) {\n  try { $r = Invoke-WebRequest -Uri (\"http://localhost\" + $path) -UseBasicParsing -TimeoutSec 30; if ($r.StatusCode -lt 400) { exit 0 } } catch { Start-Sleep -Seconds 6 }\n}\nWrite-Error \"A aplicação não respondeu em http://localhost$path\"\nexit 1\n"
            : isService ? $"$s = Get-Service -Name \"{serviceName}\"\nif ($s.Status -ne 'Running') {{ Write-Error \"Serviço {serviceName} não está em execução\"; exit 1 }}\n"
            : $"schtasks /Query /TN \"{taskName}\" | Out-Null\nif ($LASTEXITCODE -ne 0) {{ Write-Error \"Tarefa agendada não registrada\"; exit 1 }}\n";
        yield return ("scripts/validate-service.ps1", validate);
    }

    private static string ServiceName(Deployable d)
    {
        var sources = d.Result.Project.SourceFiles.Where(f => File.Exists(f.FullPath)).Select(f => (Path.GetFileName(f.FullPath), TextFiles.Read(f.FullPath).Text)).ToList();
        var info = WorkerServiceRewriter.Discover(sources);
        return info.Services.Select(s => info.ServiceNames.GetValueOrDefault(s.ClassName)).FirstOrDefault(n => n != null) ?? d.Result.Project.Name;
    }

    // ------------------------------------------------------------------ workflow (MSBuild on a Windows runner + CodeDeploy)

    private static string WorkflowWindows(SolutionResult result, string feature, List<Deployable> deployables)
    {
        var solution = Directory.EnumerateFiles(result.RootDir, "*.sln").Select(Path.GetFileName).FirstOrDefault() ?? Directory.EnumerateFiles(result.RootDir, "*.slnx").Select(Path.GetFileName).FirstOrDefault() ?? "";
        var sb = new StringBuilder();
        sb.AppendLine($"# Gerado pelo Migrator: build com MSBuild (.NET Framework 4.8.1), pacote no bucket de artefatos e deploy pelo CodeDeploy para {result.SolutionName}.");
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
        sb.AppendLine();
        sb.AppendLine("jobs:");
        sb.AppendLine("  build-deploy:");
        sb.AppendLine("    runs-on: windows-latest");
        sb.AppendLine("    strategy:");
        sb.AppendLine("      fail-fast: false");
        sb.AppendLine("      matrix:");
        sb.AppendLine("        include:");
        foreach (var d in deployables)
        {
            sb.AppendLine($"          - micro: {Micro(result, d)}");
            sb.AppendLine($"            project: {SourceDir}/{d.Result.OutputProjectPath!.Replace('\\', '/')}");
            sb.AppendLine($"            kind: {(d.Result.Project.Kind == ProjectKind.Web ? "web" : "exe")}");
        }
        sb.AppendLine("    steps:");
        sb.AppendLine("      - uses: actions/checkout@v4");
        sb.AppendLine("      - uses: microsoft/setup-msbuild@v2");
        sb.AppendLine("      - uses: nuget/setup-nuget@v2");
        sb.AppendLine($"      - run: nuget restore {(solution.Length > 0 ? SourceDir + "/" + solution : "${{ matrix.project }}")}");
        sb.AppendLine("      - name: Publish (web)");
        sb.AppendLine("        if: matrix.kind == 'web'");
        sb.AppendLine("        run: msbuild ${{ matrix.project }} /p:Configuration=Release /p:DeployOnBuild=true /p:WebPublishMethod=FileSystem /p:PublishUrl=${{ github.workspace }}\\bundle\\app /p:DeleteExistingFiles=true /p:MarkWebConfigAssistFilesAsExclude=false");
        sb.AppendLine("      - name: Build (serviço/console)");
        sb.AppendLine("        if: matrix.kind == 'exe'");
        sb.AppendLine("        run: msbuild ${{ matrix.project }} /p:Configuration=Release /p:OutDir=${{ github.workspace }}\\bundle\\app\\");
        sb.AppendLine("      - name: Montar pacote do CodeDeploy");
        sb.AppendLine("        run: Copy-Item -Path infra/codedeploy/${{ matrix.micro }}/* -Destination bundle -Recurse -Force");
        sb.AppendLine("      - uses: aws-actions/configure-aws-credentials@v4");
        sb.AppendLine("        with:");
        sb.AppendLine("          role-to-assume: ${{ secrets.AWS_ROLE_ARN }}");
        sb.AppendLine("          aws-region: ${{ env.AWS_REGION }}");
        sb.AppendLine("      - name: Publicar e implantar");
        sb.AppendLine("        run: |");
        sb.AppendLine("          $account = aws sts get-caller-identity --query Account --output text");
        sb.AppendLine("          $bucket = \"${{ env.FEATURE_NAME }}-${{ env.ENVIRONMENT }}-$account-deploy\"");
        sb.AppendLine("          $key = \"${{ matrix.micro }}/${{ github.sha }}.zip\"");
        sb.AppendLine("          $app = \"${{ env.FEATURE_NAME }}-${{ env.ENVIRONMENT }}-${{ matrix.micro }}\"");
        sb.AppendLine("          aws deploy push --application-name $app --s3-location \"s3://$bucket/$key\" --source bundle --ignore-hidden-files");
        sb.AppendLine("          $id = aws deploy create-deployment --application-name $app --deployment-group-name $app --s3-location \"bucket=$bucket,key=$key,bundleType=zip\" --query deploymentId --output text");
        sb.AppendLine("          aws deploy wait deployment-successful --deployment-id $id");
        return Yaml(sb);
    }
}
