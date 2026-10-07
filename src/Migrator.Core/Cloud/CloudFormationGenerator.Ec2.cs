using System.Text;
using Migrator.Core.Analysis;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>EC2 Windows compute stack, CodeDeploy bundles and the Windows build workflow for the .NET Framework target.</summary>
public static partial class CloudFormationGenerator
{
    private static string ComputeEc2(SolutionResult result, List<Deployable> deployables, List<Deployable> webs, HashSet<string> components, bool hasDb, bool hasS3)
    {
        var sb = new StringBuilder();
        sb.AppendLine("""
            AWSTemplateFormatVersion: "2010-09-09"
            Description: "Compute (EC2 Windows Server 2022 em Auto Scaling, ALB, CodeDeploy, alarmes) para aplicações .NET Framework 4.8.1 sem alteração de código. Gerado pelo Migrator."

            Parameters:
              AppName:
                Type: String
              Environment:
                Type: String
                Default: prod
                AllowedValues: [dev, hml, prod]
              InstanceType:
                Type: String
                Default: t3.medium
              LatestAmiId:
                Type: AWS::SSM::Parameter::Value<AWS::EC2::Image::Id>
                Default: /aws/service/ami-windows-latest/Windows_Server-2022-English-Full-Base
                Description: "AMI Windows mais recente publicada pela AWS; troque pela AMI do EC2 Image Builder quando houver."
              CertificateArn:
                Type: String
                Default: ""
                Description: ARN de um certificado no ACM para HTTPS no ALB. Vazio = só HTTP (apenas para testes).
              AlertEmail:
                Type: String
                Default: ""
                Description: E-mail que recebe os alarmes (vazio = sem assinatura).
              HealthCheckPath:
                Type: String
                Default: /
                Description: "Caminho que responde 200 sem autenticação (ex.: /health.aspx)."
              TimeZone:
                Type: String
                Default: E. South America Standard Time
                Description: Fuso horário das instâncias (tzutil), para manter DateTime.Now como on-premises.
              WebDesiredCapacity:
                Type: Number
                Default: 2
                Description: Instâncias por aplicação web (2 = alta disponibilidade entre AZs).
            """);
        foreach (var w in webs)
            sb.AppendLine($$"""
                  Host{{w.Logical}}:
                    Type: String
                    Default: {{w.Slug}}.exemplo.com.br
                    Description: Host header de {{w.Result.Project.Name}} no ALB.
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
                Properties:
                  TopicArn: !Ref AlertsTopic
                  Protocol: email
                  Endpoint: !Ref AlertEmail
              # Pacotes do CodeDeploy (zip gerado pelo workflow).
              ArtifactsBucket:
                Type: AWS::S3::Bucket
                Properties:
                  BucketName: !Sub "${AppName}-${Environment}-${AWS::AccountId}-deploy"
                  PublicAccessBlockConfiguration: { BlockPublicAcls: true, BlockPublicPolicy: true, IgnorePublicAcls: true, RestrictPublicBuckets: true }
                  VersioningConfiguration: { Status: Enabled }
                  LifecycleConfiguration:
                    Rules: [{ Id: expira-pacotes, Status: Enabled, ExpirationInDays: 90 }]
              # Instance profile: Session Manager, CloudWatch agent, segredos do prefixo da aplicação e os buckets.
              InstanceRole:
                Type: AWS::IAM::Role
                Properties:
                  RoleName: !Sub "${AppName}-${Environment}-ec2"
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
                            Resource: !Sub "arn:aws:secretsmanager:${AWS::Region}:${AWS::AccountId}:secret:${AppName}/*"
                          - Effect: Allow
                            Action: [ssm:GetParameter, ssm:GetParameters, ssm:GetParametersByPath]
                            Resource: !Sub "arn:aws:ssm:${AWS::Region}:${AWS::AccountId}:parameter/${AppName}/${Environment}/*"
                          - Effect: Allow
                            Action: [s3:GetObject, s3:ListBucket]
                            Resource: [!GetAtt ArtifactsBucket.Arn, !Sub "${ArtifactsBucket.Arn}/*"]
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
        if (components.Contains("ses"))
            sb.AppendLine("""
                              - Effect: Allow
                                Action: [ses:SendEmail, ses:SendRawEmail]
                                Resource: "*"
                """);
        sb.AppendLine("""
              InstanceProfile:
                Type: AWS::IAM::InstanceProfile
                Properties:
                  Roles: [!Ref InstanceRole]
              CodeDeployRole:
                Type: AWS::IAM::Role
                Properties:
                  RoleName: !Sub "${AppName}-${Environment}-codedeploy"
                  AssumeRolePolicyDocument:
                    Version: "2012-10-17"
                    Statement: [{ Effect: Allow, Principal: { Service: codedeploy.amazonaws.com }, Action: sts:AssumeRole }]
                  ManagedPolicyArns: [arn:aws:iam::aws:policy/service-role/AWSCodeDeployRole]
            """);
        if (webs.Count > 0)
            sb.AppendLine("""
                  AlbSecurityGroup:
                    Type: AWS::EC2::SecurityGroup
                    Properties:
                      GroupDescription: ALB
                      VpcId: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-VpcId" }
                      SecurityGroupIngress:
                        - { IpProtocol: tcp, FromPort: 80, ToPort: 80, CidrIp: 0.0.0.0/0 }
                        - { IpProtocol: tcp, FromPort: 443, ToPort: 443, CidrIp: 0.0.0.0/0 }
                """);
        sb.AppendLine("""
              InstanceSecurityGroup:
                Type: AWS::EC2::SecurityGroup
                Properties:
                  GroupDescription: "Instancias da aplicacao (sem RDP; use o Session Manager)"
                  VpcId: !ImportValue { "Fn::Sub": "${AppName}-${Environment}-VpcId" }
            """);
        if (webs.Count > 0)
            sb.AppendLine("""
                      SecurityGroupIngress:
                        - { IpProtocol: tcp, FromPort: 80, ToPort: 80, SourceSecurityGroupId: !Ref AlbSecurityGroup }
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
                      SourceSecurityGroupId: !Ref InstanceSecurityGroup
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
                      LoadBalancerAttributes: [{ Key: idle_timeout.timeout_seconds, Value: "120" }]
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
                          Port: 80
                          Protocol: HTTP
                          TargetType: instance
                          HealthCheckPath: !Ref HealthCheckPath
                          HealthCheckIntervalSeconds: 30
                          HealthyThresholdCount: 2
                          UnhealthyThresholdCount: 3
                          Matcher: { HttpCode: 200-399 }
                          TargetGroupAttributes:
                            - { Key: deregistration_delay.timeout_seconds, Value: "30" }
                    {{(w.Profile.Has(Signal.InProcSession) ? "        - { Key: stickiness.enabled, Value: \"true\" } # sessão InProc: mantém o usuário na mesma instância\n        - { Key: stickiness.type, Value: lb_cookie }" : "        - { Key: stickiness.enabled, Value: \"false\" }")}}
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
                      {{w.Logical}}UnhealthyAlarm:
                        Type: AWS::CloudWatch::Alarm
                        Properties:
                          AlarmName: !Sub "${AppName}-${Environment}-{{w.Slug}}-unhealthy"
                          Namespace: AWS/ApplicationELB
                          MetricName: UnHealthyHostCount
                          Dimensions:
                            - { Name: LoadBalancer, Value: !GetAtt LoadBalancer.LoadBalancerFullName }
                            - { Name: TargetGroup, Value: !GetAtt {{w.Logical}}TargetGroup.TargetGroupFullName }
                          Statistic: Maximum
                          Period: 60
                          EvaluationPeriods: 3
                          Threshold: 0
                          ComparisonOperator: GreaterThanThreshold
                          TreatMissingData: notBreaching
                          AlarmActions: [!Ref AlertsTopic]
                    """);
                priority += 10;
            }
        }

        foreach (var d in deployables)
        {
            var isWeb = d.Result.Project.Kind == ProjectKind.Web;
            sb.AppendLine($$"""
                  {{d.Logical}}LogGroup:
                    Type: AWS::Logs::LogGroup
                    Properties:
                      LogGroupName: !Sub "/${AppName}/${Environment}/{{d.Slug}}"
                      RetentionInDays: 30
                  {{d.Logical}}LaunchTemplate:
                    Type: AWS::EC2::LaunchTemplate
                    Properties:
                      LaunchTemplateName: !Sub "${AppName}-${Environment}-{{d.Slug}}"
                      LaunchTemplateData:
                        ImageId: !Ref LatestAmiId
                        InstanceType: !Ref InstanceType
                        IamInstanceProfile: { Arn: !GetAtt InstanceProfile.Arn }
                        SecurityGroupIds: [!Ref InstanceSecurityGroup]
                        MetadataOptions: { HttpTokens: required }
                        BlockDeviceMappings:
                          - DeviceName: /dev/sda1
                            Ebs: { VolumeSize: 60, VolumeType: gp3, Encrypted: true, DeleteOnTermination: true }
                        TagSpecifications:
                          - ResourceType: instance
                            Tags:
                              - { Key: Name, Value: !Sub "${AppName}-${Environment}-{{d.Slug}}" }
                              - { Key: Application, Value: !Ref AppName }
                              - { Key: Environment, Value: !Ref Environment }
                        UserData:
                          Fn::Base64: !Sub |
                """);
            foreach (var line in UserData(d, isWeb).Split('\n')) sb.AppendLine(line.Length == 0 ? "" : "            " + line);
            sb.AppendLine($$"""
                  {{d.Logical}}AutoScalingGroup:
                    Type: AWS::AutoScaling::AutoScalingGroup
                    UpdatePolicy:
                      AutoScalingRollingUpdate: { MinInstancesInService: {{(isWeb ? 1 : 0)}}, MaxBatchSize: 1, PauseTime: PT10M }
                    Properties:
                      AutoScalingGroupName: !Sub "${AppName}-${Environment}-{{d.Slug}}"
                      VPCZoneIdentifier: !Split [",", !ImportValue { "Fn::Sub": "${AppName}-${Environment}-PrivateSubnets" }]
                      LaunchTemplate:
                        LaunchTemplateId: !Ref {{d.Logical}}LaunchTemplate
                        Version: !GetAtt {{d.Logical}}LaunchTemplate.LatestVersionNumber
                      MinSize: "1"
                      MaxSize: "{{(isWeb ? 4 : 1)}}"
                      DesiredCapacity: {{(isWeb ? "!Ref WebDesiredCapacity" : "\"1\"")}}
                      HealthCheckType: {{(isWeb ? "ELB" : "EC2")}}
                      HealthCheckGracePeriod: 900
                {{(isWeb ? $"      TargetGroupARNs: [!Ref {d.Logical}TargetGroup]" : "      # Serviço/console: uma instância; o grupo só recria a instância em falha (sem escala horizontal).")}}
                      Tags:
                        - { Key: Name, Value: !Sub "${AppName}-${Environment}-{{d.Slug}}", PropagateAtLaunch: true }
                        - { Key: CodeDeployGroup, Value: !Sub "${AppName}-${Environment}-{{d.Slug}}", PropagateAtLaunch: true }
                  {{d.Logical}}CodeDeployApplication:
                    Type: AWS::CodeDeploy::Application
                    Properties:
                      ApplicationName: !Sub "${AppName}-${Environment}-{{d.Slug}}"
                      ComputePlatform: Server
                  {{d.Logical}}DeploymentGroup:
                    Type: AWS::CodeDeploy::DeploymentGroup
                    Properties:
                      ApplicationName: !Ref {{d.Logical}}CodeDeployApplication
                      DeploymentGroupName: !Sub "${AppName}-${Environment}-{{d.Slug}}"
                      ServiceRoleArn: !GetAtt CodeDeployRole.Arn
                      AutoScalingGroups: [!Ref {{d.Logical}}AutoScalingGroup]
                      DeploymentConfigName: CodeDeployDefault.OneAtATime
                      DeploymentStyle: { DeploymentType: IN_PLACE, DeploymentOption: {{(isWeb ? "WITH_TRAFFIC_CONTROL" : "WITHOUT_TRAFFIC_CONTROL")}} }
                {{(isWeb ? $"      LoadBalancerInfo: {{ TargetGroupInfoList: [{{ Name: !GetAtt {d.Logical}TargetGroup.TargetGroupName }}] }}" : "")}}
                      AutoRollbackConfiguration: { Enabled: true, Events: [DEPLOYMENT_FAILURE] }
                  {{d.Logical}}CpuAlarm:
                    Type: AWS::CloudWatch::Alarm
                    Properties:
                      AlarmName: !Sub "${AppName}-${Environment}-{{d.Slug}}-cpu"
                      Namespace: AWS/EC2
                      MetricName: CPUUtilization
                      Dimensions: [{ Name: AutoScalingGroupName, Value: !Ref {{d.Logical}}AutoScalingGroup }]
                      Statistic: Average
                      Period: 300
                      EvaluationPeriods: 3
                      Threshold: 85
                      ComparisonOperator: GreaterThanThreshold
                      AlarmActions: [!Ref AlertsTopic]
                """);
            if (isWeb)
                sb.AppendLine($$"""
                      {{d.Logical}}ScalingPolicy:
                        Type: AWS::AutoScaling::ScalingPolicy
                        Properties:
                          AutoScalingGroupName: !Ref {{d.Logical}}AutoScalingGroup
                          PolicyType: TargetTrackingScaling
                          TargetTrackingConfiguration:
                            PredefinedMetricSpecification: { PredefinedMetricType: ASGAverageCPUUtilization }
                            TargetValue: 60
                    """);
        }

        sb.AppendLine();
        sb.AppendLine("Outputs:");
        sb.AppendLine("  ArtifactsBucket:");
        sb.AppendLine("    Value: !Ref ArtifactsBucket");
        if (webs.Count > 0)
        {
            sb.AppendLine("  AlbDnsName:");
            sb.AppendLine("    Description: Aponte os hosts para este nome (Route 53 alias).");
            sb.AppendLine("    Value: !GetAtt LoadBalancer.DNSName");
        }
        foreach (var d in deployables)
        {
            sb.AppendLine($"  {d.Logical}CodeDeploy:");
            sb.AppendLine($"    Value: !Sub \"aws deploy create-deployment --application-name ${{{d.Logical}CodeDeployApplication}} --deployment-group-name ${{{d.Logical}DeploymentGroup}} --s3-location bucket=${{ArtifactsBucket}},key={d.Slug}/<sha>.zip,bundleType=zip\"");
        }
        return Yaml(sb);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd('-');

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
        sb.AppendLine($"  \"windows_events\": {{ \"collect_list\": [ {{ \"event_name\": \"Application\", \"event_levels\": [\"ERROR\",\"WARNING\",\"INFORMATION\"], \"log_group_name\": \"/${{AppName}}/${{Environment}}/{d.Slug}\", \"log_stream_name\": \"{{instance_id}}/eventlog\" }} ] }},");
        sb.AppendLine($"  \"files\": {{ \"collect_list\": [ {{ \"file_path\": \"{(isWeb ? $"C:\\\\inetpub\\\\{d.Slug}\\\\logs\\\\**" : $"C:\\\\apps\\\\{d.Slug}\\\\logs\\\\**")}\", \"log_group_name\": \"/${{AppName}}/${{Environment}}/{d.Slug}\", \"log_stream_name\": \"{{instance_id}}/app\" }} ] }}");
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

    private static IEnumerable<(string Path, string Content)> CodeDeployBundle(string app, Deployable d)
    {
        var isWeb = d.Result.Project.Kind == ProjectKind.Web;
        var isService = d.Result.Project.Kind == ProjectKind.WindowsService;
        var assembly = string.IsNullOrEmpty(d.Result.Project.AssemblyName) ? d.Result.Project.Name : d.Result.Project.AssemblyName;
        var root = isWeb ? $"C:\\inetpub\\{d.Slug}" : $"C:\\apps\\{d.Slug}";
        var serviceName = isService ? ServiceName(d) : null;
        var configFile = isWeb ? "web.config" : $"{assembly}.exe.config";
        var secretId = $"{app}/{d.Slug}/config";

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
        else before.AppendLine($"schtasks /End /TN \"{app}-{d.Slug}\" 2>$null | Out-Null");
        yield return ("scripts/before-install.ps1", before.ToString().Replace("\r\n", "\n"));

        var after = new StringBuilder();
        after.AppendLine("$ErrorActionPreference = \"Stop\"");
        after.AppendLine($"$root = \"{root}\"");
        after.AppendLine($"$config = Join-Path $root \"{configFile}\"");
        after.AppendLine("New-Item -ItemType Directory -Path (Join-Path $root \"logs\") -Force | Out-Null");
        after.AppendLine("# Segredos: o segredo <app>/<projeto>/config (JSON chave→valor) é gravado no config da instância; nada no repositório.");
        after.AppendLine("Import-Module AWSPowerShell -ErrorAction SilentlyContinue");
        after.AppendLine($"$secretId = \"{secretId}\"");
        after.AppendLine("try { $json = (Get-SECSecretValue -SecretId $secretId).SecretString } catch { Write-Host \"Segredo $secretId não encontrado; config mantido como no pacote\"; $json = $null }");
        after.AppendLine("if ($json -and (Test-Path $config)) {");
        after.AppendLine("  $values = $json | ConvertFrom-Json");
        after.AppendLine("  [xml]$xml = Get-Content $config");
        after.AppendLine("  foreach ($p in $values.PSObject.Properties) {");
        after.AppendLine("    if ($p.Name -like 'ConnectionStrings:*') {");
        after.AppendLine("      $node = $xml.SelectSingleNode(\"//connectionStrings/add[@name='\" + $p.Name.Substring(18) + \"']\")");
        after.AppendLine("      if ($node) { $node.SetAttribute('connectionString', $p.Value) }");
        after.AppendLine("    } else {");
        after.AppendLine("      $key = $p.Name -replace '^AppSettings:', ''");
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
            after.AppendLine($"schtasks /Create /F /SC MINUTE /MO 15 /TN \"{app}-{d.Slug}\" /TR \"`\"$exe`\"\" /RU SYSTEM /RL HIGHEST | Out-Null");
        }
        yield return ("scripts/after-install.ps1", after.ToString().Replace("\r\n", "\n"));

        var start = isWeb ? $"$ErrorActionPreference = \"Stop\"\nImport-Module WebAdministration\nStart-WebAppPool -Name \"{d.Slug}\"\nStart-Website -Name \"{d.Slug}\"\n"
            : isService ? $"$ErrorActionPreference = \"Stop\"\nStart-Service -Name \"{serviceName}\"\n"
            : $"# Tarefa agendada: o Agendador dispara no próximo intervalo. Execução imediata opcional:\nschtasks /Run /TN \"{app}-{d.Slug}\" | Out-Null\n";
        yield return ("scripts/application-start.ps1", start);

        var validate = isWeb
            ? "$ErrorActionPreference = \"Stop\"\n$path = \"/\" # mesmo caminho do parâmetro HealthCheckPath da stack\nfor ($i = 0; $i -lt 10; $i++) {\n  try { $r = Invoke-WebRequest -Uri (\"http://localhost\" + $path) -UseBasicParsing -TimeoutSec 30; if ($r.StatusCode -lt 400) { exit 0 } } catch { Start-Sleep -Seconds 6 }\n}\nWrite-Error \"A aplicação não respondeu em http://localhost$path\"\nexit 1\n"
            : isService ? $"$s = Get-Service -Name \"{serviceName}\"\nif ($s.Status -ne 'Running') {{ Write-Error \"Serviço {serviceName} não está em execução\"; exit 1 }}\n"
            : $"schtasks /Query /TN \"{app}-{d.Slug}\" | Out-Null\nif ($LASTEXITCODE -ne 0) {{ Write-Error \"Tarefa agendada não registrada\"; exit 1 }}\n";
        yield return ("scripts/validate-service.ps1", validate);
    }

    private static string ServiceName(Deployable d)
    {
        var sources = d.Result.Project.SourceFiles.Where(f => File.Exists(f.FullPath)).Select(f => (Path.GetFileName(f.FullPath), TextFiles.Read(f.FullPath).Text)).ToList();
        var info = WorkerServiceRewriter.Discover(sources);
        return info.Services.Select(s => info.ServiceNames.GetValueOrDefault(s.ClassName)).FirstOrDefault(n => n != null) ?? d.Result.Project.Name;
    }

    // ------------------------------------------------------------------ workflow (MSBuild on a Windows runner + CodeDeploy)

    private static string WorkflowWindows(string app, SolutionResult result, List<Deployable> deployables)
    {
        var solution = Directory.EnumerateFiles(result.RootDir, "*.sln").Select(Path.GetFileName).FirstOrDefault() ?? Directory.EnumerateFiles(result.RootDir, "*.slnx").Select(Path.GetFileName).FirstOrDefault() ?? "";
        var sb = new StringBuilder();
        sb.AppendLine($"# Gerado pelo Migrator: build com MSBuild (.NET Framework 4.8.1), pacote no S3 e deploy pelo CodeDeploy para {result.SolutionName}.");
        sb.AppendLine("# Pré-requisitos: role IAM para GitHub OIDC em AWS_ROLE_ARN (secret), stacks criadas com infra/cloudformation/deploy.sh.");
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
        sb.AppendLine($"  APP_NAME: {app}");
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
            sb.AppendLine($"          - name: {d.Slug}");
            sb.AppendLine($"            project: {d.Result.OutputProjectPath!.Replace('\\', '/')}");
            sb.AppendLine($"            kind: {(d.Result.Project.Kind == ProjectKind.Web ? "web" : "exe")}");
        }
        sb.AppendLine("    steps:");
        sb.AppendLine("      - uses: actions/checkout@v4");
        sb.AppendLine("      - uses: microsoft/setup-msbuild@v2");
        sb.AppendLine("      - uses: nuget/setup-nuget@v2");
        sb.AppendLine($"      - run: nuget restore {(solution.Length > 0 ? solution : "${{ matrix.project }}")}");
        sb.AppendLine("      - name: Publish (web)");
        sb.AppendLine("        if: matrix.kind == 'web'");
        sb.AppendLine("        run: msbuild ${{ matrix.project }} /p:Configuration=Release /p:DeployOnBuild=true /p:WebPublishMethod=FileSystem /p:PublishUrl=${{ github.workspace }}\\bundle\\app /p:DeleteExistingFiles=true /p:MarkWebConfigAssistFilesAsExclude=false");
        sb.AppendLine("      - name: Build (serviço/console)");
        sb.AppendLine("        if: matrix.kind == 'exe'");
        sb.AppendLine("        run: msbuild ${{ matrix.project }} /p:Configuration=Release /p:OutDir=${{ github.workspace }}\\bundle\\app\\");
        sb.AppendLine("      - name: Montar pacote do CodeDeploy");
        sb.AppendLine("        run: Copy-Item -Path infra/codedeploy/${{ matrix.name }}/* -Destination bundle -Recurse -Force");
        sb.AppendLine("      - uses: aws-actions/configure-aws-credentials@v4");
        sb.AppendLine("        with:");
        sb.AppendLine("          role-to-assume: ${{ secrets.AWS_ROLE_ARN }}");
        sb.AppendLine("          aws-region: ${{ env.AWS_REGION }}");
        sb.AppendLine("      - name: Publicar e implantar");
        sb.AppendLine("        run: |");
        sb.AppendLine("          $account = aws sts get-caller-identity --query Account --output text");
        sb.AppendLine("          $bucket = \"${{ env.APP_NAME }}-${{ env.ENVIRONMENT }}-$account-deploy\"");
        sb.AppendLine("          $key = \"${{ matrix.name }}/${{ github.sha }}.zip\"");
        sb.AppendLine("          $app = \"${{ env.APP_NAME }}-${{ env.ENVIRONMENT }}-${{ matrix.name }}\"");
        sb.AppendLine("          aws deploy push --application-name $app --s3-location \"s3://$bucket/$key\" --source bundle --ignore-hidden-files");
        sb.AppendLine("          $id = aws deploy create-deployment --application-name $app --deployment-group-name $app --s3-location \"bucket=$bucket,key=$key,bundleType=zip\" --query deploymentId --output text");
        sb.AppendLine("          aws deploy wait deployment-successful --deployment-id $id");
        return Yaml(sb);
    }
}
