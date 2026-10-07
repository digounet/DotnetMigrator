# Atualização para .NET Framework 4.8.1 + infraestrutura AWS — LegacyShop

- **Origem:** `/Users/pablo/Source/dotnet/DotnetMigrator/DotnetMigrator/samples/LegacyShop`
- **Modo:** Análise (nenhum arquivo alterado)
- **Destino:** .NET Framework 4.8.1
- **Gerado em:** 07/10/2026 08:01
- **Compatibilidade NuGet verificada:** não (feed: nuget.org (https://api.nuget.org/v3/index.json))
- **Build de verificação:** não executado

- [1. Resumo](#1-resumo)
- [2. Guia de implantação na AWS](#2-guia-de-implantação-na-aws)
- [3. Arquitetura alvo (AWS)](#3-arquitetura-alvo-aws)
- [4. Dados acessados (bancos, tabelas e campos)](#4-dados-acessados-bancos-tabelas-e-campos)
- [5. Modernização](#5-modernização)
- [6. Inventário da migração](#6-inventário-da-migração)

## 1. Resumo

| Projeto | Tipo | Origem | Bloqueantes | Atenção | Automático | % automatizado | Build | Modernização | AWS |
|---|---|---|---:|---:|---:|---:|---|---:|---|
| LegacyShop.Web | Web (MVC/Web API) | v4.7.2 | 0 | 1 | 5 | 83% | não executado (análise) | 19 | EC2 Windows |
| LegacyShop.Core | Biblioteca | v4.6.1 | 0 | 0 | 1 | 100% | não executado (análise) | 5 | — |
| LegacyShop.Worker | Windows Service | v4.6.1 | 0 | 0 | 2 | 100% | não executado (análise) | 6 | EC2 Windows |
| LegacyShop.Tests | Testes | v4.6.1 | 0 | 0 | 1 | 100% | não executado (análise) | 0 | — |
| LegacyShop.Importador | Console | v4.5 | 0 | 1 | 2 | 67% | não executado (análise) | 5 | EC2 Windows |
| LegacyShop.Relatorios | Console · VB.NET | v4.5 | 0 | 1 | 1 | 50% | não executado (análise) | 4 | EC2 Windows |

## 2. Guia de implantação na AWS

Tudo o que precisa ser configurado para **LegacyShop** rodar na AWS: destino **.NET Framework 4.8.1**, infraestrutura em **CloudFormation** (feature `legacyshop`, ambientes dev, hom, prod). Os arquivos citados são gerados pelo `migrate`; esta execução só os descreve. Valores marcados com ⚠ são exemplos e precisam ser substituídos antes do primeiro deploy.

### 2.1 O que roda onde

| Projeto | Tipo | Hospedagem | Como roda | Template → stack | Endpoint / health / agendamento | Antes do primeiro deploy |
|---|---|---|---|---|---|---|
| **LegacyShop.Web** (`web`) | Web (MVC/Web API) | EC2 Windows | site no IIS (app pool próprio) em instâncias EC2 Windows com Auto Scaling, atrás do ALB compartilhado; deploy pelo CodeDeploy | `service.yml` → `legacyshop-<env>-web` (`parameters.json`) | https://legacyshop-web.empresa.com.br (ListenerRuleHost; registre no Route 53)<br>GET HealthCheckPath (padrão "/"); responda 200 sem autenticação | • Endpoint de health check para o ALB (página/action que responda 200 sem autenticação, ex.: /health.aspx ou /health); informe o caminho no parâmetro HealthCheckPath da stack.<br>• machineKey explícita no web.config, igual em todas as instâncias (Forms Authentication/ViewState com mais de uma instância atrás do ALB).<br>• Sessão InProc não é compartilhada entre instâncias: habilite stickiness no target group (menos resiliente) ou mova a sessão para SQL Server (aspnet_regsql) / Redis (provider).<br>• Pastas locais (C:\..., App_Data) ficam no disco EBS da instância e se perdem na troca por Auto Scaling: aponte para FSx/File Gateway ou aceite instância única.<br>• Senhas fora do repositório: o script after-install.ps1 do CodeDeploy lê o segredo &lt;app&gt;/&lt;projeto&gt;/config no Secrets Manager e grava no web.config/app.config da instância; rotacione as credenciais que estavam em texto claro.<br>• SMTP interno → Amazon SES (endpoint SMTP na porta 587, credenciais SMTP no Secrets Manager) ou relay pela VPN.<br>• Logs: CloudWatch agent (instalado no user data) coleta Event Log e arquivos de log para o CloudWatch Logs.<br>• Fuso horário da instância definido no user data (tzutil) para manter DateTime.Now como on-premises. |
| **LegacyShop.Worker** (`worker`) | Windows Service | EC2 Windows | serviço Windows instalado pelo CodeDeploy em EC2 Windows (instância única) | `service-worker.yml` → `legacyshop-<env>-worker` (`parameters-worker.json`) | — | • Pastas locais (C:\..., App_Data) ficam no disco EBS da instância e se perdem na troca por Auto Scaling: aponte para FSx/File Gateway ou aceite instância única.<br>• Integrated Security exige instâncias ingressadas no domínio (AWS Managed Microsoft AD ou AD Connector + VPN) e RDS com autenticação Windows; alternativa simples: autenticação SQL + Secrets Manager.<br>• SMTP interno → Amazon SES (endpoint SMTP na porta 587, credenciais SMTP no Secrets Manager) ou relay pela VPN.<br>• Logs: CloudWatch agent (instalado no user data) coleta Event Log e arquivos de log para o CloudWatch Logs.<br>• Fuso horário da instância definido no user data (tzutil) para manter DateTime.Now como on-premises. |
| **LegacyShop.Importador** (`importador`) | Console | EC2 Windows | tarefa do Agendador de Tarefas (console) em EC2 Windows, criada pelo CodeDeploy | `service-importador.yml` → `legacyshop-<env>-importador` (`parameters-importador.json`) | gatilho do Agendador de Tarefas definido em codedeploy/&lt;micro&gt;/application-start.ps1 (a cada 15 min por padrão) | • Compartilhamentos \\servidor\pasta → Amazon FSx for Windows File Server (SMB nativo, exige Active Directory) ou AWS Storage Gateway (File Gateway: SMB sobre S3), sem mudar o código; copie os dados com robocopy/DataSync.<br>• Senhas fora do repositório: o script after-install.ps1 do CodeDeploy lê o segredo &lt;app&gt;/&lt;projeto&gt;/config no Secrets Manager e grava no web.config/app.config da instância; rotacione as credenciais que estavam em texto claro.<br>• EWS está sendo desligado no Exchange Online: a leitura da caixa postal deve migrar para Microsoft Graph (o SDK roda no .NET Framework 4.8.1); se o Exchange for on-premises, liberar o acesso pela VPN.<br>• SMTP interno → Amazon SES (endpoint SMTP na porta 587, credenciais SMTP no Secrets Manager) ou relay pela VPN.<br>• Fuso horário da instância definido no user data (tzutil) para manter DateTime.Now como on-premises. |
| **LegacyShop.Relatorios** (`relatorios`) | Console · VB.NET | EC2 Windows ⚠ exige Windows | tarefa do Agendador de Tarefas (console) em EC2 Windows, criada pelo CodeDeploy | `service-relatorios.yml` → `legacyshop-<env>-relatorios` (`parameters-relatorios.json`) | gatilho do Agendador de Tarefas definido em codedeploy/&lt;micro&gt;/application-start.ps1 (a cada 15 min por padrão) | • Compartilhamentos \\servidor\pasta → Amazon FSx for Windows File Server (SMB nativo, exige Active Directory) ou AWS Storage Gateway (File Gateway: SMB sobre S3), sem mudar o código; copie os dados com robocopy/DataSync.<br>• Senhas fora do repositório: o script after-install.ps1 do CodeDeploy lê o segredo &lt;app&gt;/&lt;projeto&gt;/config no Secrets Manager e grava no web.config/app.config da instância; rotacione as credenciais que estavam em texto claro.<br>• SMTP interno → Amazon SES (endpoint SMTP na porta 587, credenciais SMTP no Secrets Manager) ou relay pela VPN.<br>• Logs: CloudWatch agent (instalado no user data) coleta Event Log e arquivos de log para o CloudWatch Logs.<br>• Fuso horário da instância definido no user data (tzutil) para manter DateTime.Now como on-premises. |

### 2.2 Infraestrutura: arquivos, ordem de deploy e parâmetros

| Arquivo | Para quê | Stack | Parâmetros | Ordem |
|---|---|---|---|---:|
| `infra/data.yml` | recursos próprios da aplicação (RDS, bucket S3 + fila de eventos, FSx for Windows, bucket de artefatos do CodeDeploy, nomes dos segredos), exportados para os serviços | `legacyshop-<env>-data` | `infra/<env>/parameters-data.json` | 1 |
| `infra/service.yml` | LegacyShop.Web: EC2 Windows | `legacyshop-<env>-web` | `infra/<env>/parameters.json` | 2 |
| `infra/service-worker.yml` | LegacyShop.Worker: EC2 Windows | `legacyshop-<env>-worker` | `infra/<env>/parameters-worker.json` | 3 |
| `infra/service-importador.yml` | LegacyShop.Importador: EC2 Windows | `legacyshop-<env>-importador` | `infra/<env>/parameters-importador.json` | 4 |
| `infra/service-relatorios.yml` | LegacyShop.Relatorios: EC2 Windows | `legacyshop-<env>-relatorios` | `infra/<env>/parameters-relatorios.json` | 5 |
| `infra/codedeploy/web/` | appspec.yml + before-install/after-install/application-start/validate-service.ps1 de LegacyShop.Web; after-install.ps1 grava Parameter Store e segredos no config da instância | — | — | — |
| `infra/codedeploy/worker/` | appspec.yml + before-install/after-install/application-start/validate-service.ps1 de LegacyShop.Worker; after-install.ps1 grava Parameter Store e segredos no config da instância | — | — | — |
| `infra/codedeploy/importador/` | appspec.yml + before-install/after-install/application-start/validate-service.ps1 de LegacyShop.Importador; after-install.ps1 grava Parameter Store e segredos no config da instância | — | — | — |
| `infra/codedeploy/relatorios/` | appspec.yml + before-install/after-install/application-start/validate-service.ps1 de LegacyShop.Relatorios; after-install.ps1 grava Parameter Store e segredos no config da instância | — | — | — |
| `infra/deploy.sh, deploy.ps1` | aws cloudformation deploy de cada stack na ordem acima com a pasta do ambiente (./deploy.sh dev\|hom\|prod [região]) | — | — | — |
| `infra/README.md` | convenções da plataforma, ordem de execução e checklist de produção | — | — | — |
| `.github/workflows/deploy.yml` | MSBuild em runner Windows → zip → CodeDeploy (referência dos comandos se a esteira da plataforma não for usada) | — | — | — |

**Parâmetros por ambiente** (10 a preencher, marcados com ⚠):

| Grupo | Parâmetro | dev | hom | prod | Arquivos | Descrição |
|---|---|---|---|---|---|---|
| Esteira | ⚠ `DevToolsAccount` | 123456789012 | 123456789012 | 123456789012 | parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Conta AWS das ferramentas da esteira (ECR das imagens / artefatos do deploy). |
| Esteira | `Environment` | dev | hom | prod | parameters-data.json, parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Ambiente (dev, hom, prod): entra nos nomes dos recursos e nos caminhos do Parameter Store. |
| Esteira | `FeatureName` | legacyshop | legacyshop | legacyshop | parameters-data.json, parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Nome da feature (aplicação), só letras minúsculas; usado pela pipeline para nomear recursos. Não altere. |
| Esteira | `MicroServiceName` | (varia por arquivo) | (varia por arquivo) | (varia por arquivo) | parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Nome do microsserviço (projeto sem o prefixo da solução), só letras minúsculas. Não altere. |
| Compartilhada | ⚠ `CodeDeployRoleArn` | — | — | — | parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Service role do CodeDeploy. Vazio = a stack cria. |
| Compartilhada | ⚠ `InstanceProfileArn` | — | — | — | parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Instance profile da plataforma (SSM, CloudWatch agent, segredos e Parameter Store do prefixo da aplicação, bucket de artefatos). Vazio = a stack cria. |
| Compartilhada | `ListenerRulePriority` | 100 | 100 | 100 | parameters.json | Prioridade da regra no listener (única por aplicação no mesmo ALB). |
| Compartilhada | ⚠ `LoadBalancerListenerArn` | arn:aws:elasticloadbalancing:sa-east-1:123456789012:listener/app/alb-compartilhado/xxxx/yyyy | arn:aws:elasticloadbalancing:sa-east-1:123456789012:listener/app/alb-compartilhado/xxxx/yyyy | arn:aws:elasticloadbalancing:sa-east-1:123456789012:listener/app/alb-compartilhado/xxxx/yyyy | parameters.json | Listener HTTPS do Application Load Balancer compartilhado onde a regra da aplicação é criada. |
| Compartilhada | ⚠ `PrivateSubnetOne` | subnet-xxxxxxxxxxxxxxxx1 | subnet-xxxxxxxxxxxxxxxx1 | subnet-xxxxxxxxxxxxxxxx1 | parameters-data.json, parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Subnet privada 1 (instâncias/tasks e RDS). |
| Compartilhada | ⚠ `PrivateSubnetThree` | subnet-xxxxxxxxxxxxxxxx3 | subnet-xxxxxxxxxxxxxxxx3 | subnet-xxxxxxxxxxxxxxxx3 | parameters-data.json, parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Subnet privada 3. |
| Compartilhada | ⚠ `PrivateSubnetTwo` | subnet-xxxxxxxxxxxxxxxx2 | subnet-xxxxxxxxxxxxxxxx2 | subnet-xxxxxxxxxxxxxxxx2 | parameters-data.json, parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Subnet privada 2. |
| Compartilhada | ⚠ `VPCID` | vpc-xxxxxxxxxxxxxxxxx | vpc-xxxxxxxxxxxxxxxxx | vpc-xxxxxxxxxxxxxxxxx | parameters-data.json, parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | ID da VPC da conta, fornecida pela plataforma. |
| Dimensionamento | `DesiredCapacity` | 1 | 1 | (varia por arquivo) | parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Instâncias desejadas no Auto Scaling group. |
| Dimensionamento | `InstancePort` | 80 | 80 | 80 | parameters.json | Porta do site no IIS (target group). |
| Dimensionamento | `InstanceType` | t3.small | t3.medium | t3.large | parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Tipo da instância EC2 Windows. |
| Dimensionamento | `MaxCapacity` | (varia por arquivo) | (varia por arquivo) | (varia por arquivo) | parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Máximo de instâncias. |
| Dimensionamento | `MinCapacity` | 1 | 1 | (varia por arquivo) | parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Mínimo de instâncias. |
| Dimensionamento | `TimeZone` | E. South America Standard Time | E. South America Standard Time | E. South America Standard Time | parameters-importador.json, parameters-relatorios.json, parameters-worker.json, parameters.json | Fuso horário das instâncias (tzutil), para manter DateTime.Now como on-premises. |
| Dados | ⚠ `ActiveDirectoryId` | — | — | — | parameters-data.json | ID do AWS Managed Microsoft AD (d-xxxx) exigido pelo FSx for Windows; vazio = não criar o FSx. |
| Dados | `DbEngine` | sqlserver-ex | sqlserver-ex | sqlserver-ex | parameters-data.json | Engine do RDS (sqlserver-ex/se/ee, oracle-se2, mysql, postgres). |
| Dados | `DbInstanceClass` | db.t3.small | db.t3.small | db.t3.large | parameters-data.json | Classe da instância RDS. |
| Aplicação | `ApiBaseUrl` | http://localhost:51234/api | http://localhost:51234/api | https://loja.exemplo.com.br/api | parameters.json | URL AppSettings:ApiBaseUrl (appSettings); revise o valor de cada ambiente |
| Aplicação | `CaixaPostal` | pedidos@exemplo.com.br | pedidos@exemplo.com.br | pedidos@exemplo.com.br | parameters-importador.json | E-mail AppSettings:CaixaPostal (appSettings); revise o valor de cada ambiente |
| Aplicação | `EmailsEmailSuporte` | suporte@exemplo.com.br | suporte@exemplo.com.br | suporte@exemplo.com.br | parameters.json | E-mail AppSettings:Emails:EmailSuporte (estava fixo no código); revise o valor de cada ambiente |
| Aplicação | `ErpEndpoint` | http://erp.interno:8080/integracao | http://erp.interno:8080/integracao | https://erp.empresa.com.br/integracao | parameters-worker.json | URL AppSettings:ErpEndpoint (appSettings); revise o valor de cada ambiente |
| Aplicação | `ExchangeUrl` | https://mail.exemplo.com.br/EWS/Exchange.asmx | https://mail.exemplo.com.br/EWS/Exchange.asmx | https://mail.exemplo.com.br/EWS/Exchange.asmx | parameters-importador.json | URL AppSettings:ExchangeUrl (appSettings); revise o valor de cada ambiente |
| Aplicação | `HealthCheckPath` | / | / | / | parameters.json | Caminho que responde 200 sem autenticação para o health check do target group. |
| Aplicação | ⚠ `ListenerRuleHost` | legacyshop-web-dev.empresa.com.br | legacyshop-web-hom.empresa.com.br | legacyshop-web.empresa.com.br | parameters.json | Host header (DNS) da aplicação; crie o registro no Route 53 apontando para o ALB. |
| Aplicação | `ListenerRulePath` | /* | /* | /* | parameters.json | Caminho roteado para esta aplicação ("/*" quando o host header já a identifica). |
| Aplicação | `SmtpFrom` | loja@exemplo.com.br | loja@exemplo.com.br | loja@exemplo.com.br | parameters.json | E-mail AppSettings:Smtp.From (appSettings); revise o valor de cada ambiente |
| Aplicação | `UrlsErpProtocoloUrl` | https://erp.exemplo.com.br/api/protocolo | https://erp.exemplo.com.br/api/protocolo | https://erp.exemplo.com.br/api/protocolo | parameters.json | URL AppSettings:Urls:ErpProtocoloUrl (estava fixo no código); revise o valor de cada ambiente |

### 2.3 Banco de dados

**LegacyShop** (SQL Server) — usado por LegacyShop.Core, LegacyShop.Importador, LegacyShop.Web, LegacyShop.Worker; 2 tabela(s) e 0 procedure(s) acessadas (detalhes na seção Dados acessados).

| Item | Valor |
|---|---|
| Connection string(s) | DefaultConnection |
| Origem | (LocalDb)\MSSQLLocalDB (Integrated Security) |
| RDS | engine `sqlserver-ex`; instância dev: `db.t3.small` / hom: `db.t3.small` / prod: `db.t3.large` |
| Endpoint | export legacyshop-&lt;env&gt;-DbEndpoint / DbPort da stack data (identificador legacyshop-&lt;env&gt;) |
| Connection string na AWS | legacyshop/legacyshop-web/config → chave ConnectionStrings:DefaultConnection (LegacyShop.Web; JSON a preencher)<br>legacyshop/legacyshop-worker/config → chave ConnectionStrings:DefaultConnection (LegacyShop.Worker; JSON a preencher)<br>legacyshop/legacyshop-importador/config → chave ConnectionStrings:DefaultConnection (LegacyShop.Importador; JSON a preencher) |
| Nota | Senha master gerenciada pelo RDS (ManageMasterUserPassword; ARN em DbMasterSecretArn): crie o usuário da aplicação e grave a connection string nova no segredo acima. |
| Nota | Migração: backup .bak no S3 + rds_restore_database (ou AWS DMS para cutover com pouca parada). Licença inclusa no RDS; Express para bases pequenas. |
| Nota | Integrated Security: exige AWS Managed Microsoft AD no RDS; prefira autenticação SQL com a senha no Secrets Manager. |

**Relatorios** (SQL Server) — usado por LegacyShop.Relatorios, LegacyShop.Web; 1 tabela(s) e 0 procedure(s) acessadas (detalhes na seção Dados acessados).

| Item | Valor |
|---|---|
| Connection string(s) | RelatoriosConnection, Relatorios |
| Origem | srv-sql01 |
| RDS | engine `sqlserver-ex`; instância dev: `db.t3.small` / hom: `db.t3.small` / prod: `db.t3.large` |
| Endpoint | export legacyshop-&lt;env&gt;-DbEndpoint / DbPort da stack data (identificador legacyshop-&lt;env&gt;) |
| Connection string na AWS | legacyshop/legacyshop-web/config → chave ConnectionStrings:RelatoriosConnection (LegacyShop.Web; JSON a preencher)<br>legacyshop/legacyshop-relatorios/config → chave ConnectionStrings:RelatoriosConnection (LegacyShop.Relatorios; JSON a preencher) |
| Nota | Senha master gerenciada pelo RDS (ManageMasterUserPassword; ARN em DbMasterSecretArn): crie o usuário da aplicação e grave a connection string nova no segredo acima. |
| Nota | Migração: backup .bak no S3 + rds_restore_database (ou AWS DMS para cutover com pouca parada). Licença inclusa no RDS; Express para bases pequenas. |
| Nota | Servidor de origem srv-sql01: enquanto o banco não migrar, a aplicação na AWS precisa de rota (VPN/Direct Connect) e DNS para ele. |

### 2.4 Segredos (Secrets Manager)

Nenhum valor passa por template, parâmetro ou repositório: os templates criam os nomes e os valores entram pelos scripts de `_secrets/` ou manualmente.

| Segredo | Conteúdo | Usado por | Como chega na aplicação | Como preencher | Criado por |
|---|---|---|---|---|---|
| `legacyshop/legacyshop-web/config` | JSON chave→valor gravado no config de LegacyShop.Web pelo CodeDeploy (ConnectionStrings:Nome, AppSettings:Chave). | LegacyShop.Web | chaves do config da instância (after-install.ps1) | manual: JSON chave→valor com as connection strings e demais chaves do web.config/app.config que não podem ficar no repositório | infra/data.yml (SecretString: PREENCHER) |
| `legacyshop/legacyshop.web/AppSettings/Credenciais/TokenIntegracaoErp` | AppSettings:Credenciais:TokenIntegracaoErp de LegacyShop.Web; valor via _secrets/LegacyShop.Web/create-secrets.sh. | LegacyShop.Web | chave AppSettings:Credenciais:TokenIntegracaoErp do config da instância (after-install.ps1) | _secrets/LegacyShop.Web/create-secrets.sh (o valor que estava no config/código fica em _secrets/, fora do git); rotacione a credencial depois | infra/data.yml (SecretString: PREENCHER) |
| `legacyshop/legacyshop.web/AppSettings/Credenciais/ConexaoRelatoriosAntiga` | AppSettings:Credenciais:ConexaoRelatoriosAntiga de LegacyShop.Web; valor via _secrets/LegacyShop.Web/create-secrets.sh. | LegacyShop.Web | chave AppSettings:Credenciais:ConexaoRelatoriosAntiga do config da instância (after-install.ps1) | _secrets/LegacyShop.Web/create-secrets.sh (o valor que estava no config/código fica em _secrets/, fora do git); rotacione a credencial depois | infra/data.yml (SecretString: PREENCHER) |
| `legacyshop/legacyshop-worker/config` | JSON chave→valor gravado no config de LegacyShop.Worker pelo CodeDeploy (ConnectionStrings:Nome, AppSettings:Chave). | LegacyShop.Worker | chaves do config da instância (after-install.ps1) | manual: JSON chave→valor com as connection strings e demais chaves do web.config/app.config que não podem ficar no repositório | infra/data.yml (SecretString: PREENCHER) |
| `legacyshop/legacyshop-importador/config` | JSON chave→valor gravado no config de LegacyShop.Importador pelo CodeDeploy (ConnectionStrings:Nome, AppSettings:Chave). | LegacyShop.Importador | chaves do config da instância (after-install.ps1) | manual: JSON chave→valor com as connection strings e demais chaves do web.config/app.config que não podem ficar no repositório | infra/data.yml (SecretString: PREENCHER) |
| `legacyshop/legacyshop-relatorios/config` | JSON chave→valor gravado no config de LegacyShop.Relatorios pelo CodeDeploy (ConnectionStrings:Nome, AppSettings:Chave). | LegacyShop.Relatorios | chaves do config da instância (after-install.ps1) | manual: JSON chave→valor com as connection strings e demais chaves do web.config/app.config que não podem ficar no repositório | infra/data.yml (SecretString: PREENCHER) |
| `rds!db-... (gerenciado; ARN em DbMasterSecretArn)` | senha master do RDS |  | não vai para a aplicação | automático (RDS); use só para criar o usuário da aplicação | infra/data.yml (ManageMasterUserPassword) |

### 2.5 Variáveis de ambiente e parâmetros da aplicação

Os valores chegam às instâncias pelo SSM Parameter Store (`/<feature>/<env>/...`), gravados no `appSettings` do config pelo `after-install.ps1` do CodeDeploy; a aplicação continua lendo `ConfigurationManager.AppSettings[...]`.

| Chave | Tipo | Entrega | Parâmetro | dev | hom | prod | Origem | Usado por |
|---|---|---|---|---|---|---|---|---|
| `AppSettings:ApiBaseUrl` | URL | `/legacyshop/<env>/ApiBaseUrl` | `ApiBaseUrl` | http://localhost:51234/api | http://localhost:51234/api | https://loja.exemplo.com.br/api | appSettings (Web.config) | LegacyShop.Web |
| `AppSettings:CaixaPostal` | E-mail | `/legacyshop/<env>/CaixaPostal` | `CaixaPostal` | pedidos@exemplo.com.br | pedidos@exemplo.com.br | pedidos@exemplo.com.br | appSettings (App.config) | LegacyShop.Importador |
| `AppSettings:Emails:EmailSuporte` | E-mail | `/legacyshop/<env>/Emails/EmailSuporte` | `EmailsEmailSuporte` | suporte@exemplo.com.br | suporte@exemplo.com.br | suporte@exemplo.com.br | fixo no código (Helpers/AppConfig.cs:11) | LegacyShop.Web |
| `AppSettings:ErpEndpoint` | URL | `/legacyshop/<env>/ErpEndpoint` | `ErpEndpoint` | http://erp.interno:8080/integracao | http://erp.interno:8080/integracao | https://erp.empresa.com.br/integracao | appSettings (App.config) | LegacyShop.Worker |
| `AppSettings:ExchangeUrl` | URL | `/legacyshop/<env>/ExchangeUrl` | `ExchangeUrl` | https://mail.exemplo.com.br/EWS/Exchange.asmx | https://mail.exemplo.com.br/EWS/Exchange.asmx | https://mail.exemplo.com.br/EWS/Exchange.asmx | appSettings (App.config) | LegacyShop.Importador |
| `AppSettings:Smtp.From` | E-mail | `/legacyshop/<env>/Smtp.From` | `SmtpFrom` | loja@exemplo.com.br | loja@exemplo.com.br | loja@exemplo.com.br | appSettings (Web.config) | LegacyShop.Web |
| `AppSettings:Urls:ErpProtocoloUrl` | URL | `/legacyshop/<env>/Urls/ErpProtocoloUrl` | `UrlsErpProtocoloUrl` | https://erp.exemplo.com.br/api/protocolo | https://erp.exemplo.com.br/api/protocolo | https://erp.exemplo.com.br/api/protocolo | fixo no código (Helpers/AppConfig.cs:10) | LegacyShop.Web |

### 2.6 Armazenamento e filas

| Serviço | Recurso | Para quê | Substitui | Usado por | Como a aplicação encontra | Notas |
|---|---|---|---|---|---|---|
| Amazon S3 | `legacyshop-<env>-<conta>-files` | arquivos da aplicação (entrada/saída, uploads, exportações) | pastas de rede / locais | LegacyShop.Importador, LegacyShop.Relatorios, LegacyShop.Web, LegacyShop.Worker | compartilhamento SMB via Storage Gateway (File Gateway) ou caminho \\&lt;gateway&gt;\&lt;bucket&gt; | • Com File Gateway o código continua lendo pastas; configure o gateway (appliance ou EC2) e monte o compartilhamento nas instâncias. |
| Amazon FSx for Windows File Server | `\\<FsxDnsName>\share (export legacyshop-<env>-FsxDnsName)` | pastas de rede (UNC) montadas nas instâncias com os mesmos nomes | pastas de rede: arquivos | LegacyShop.Importador, LegacyShop.Relatorios, LegacyShop.Web, LegacyShop.Worker | DNS do file system exportado pela stack data; mapeie no user data/CodeDeploy | • Exige ActiveDirectoryId (AWS Managed Microsoft AD) em parameters-data.json; copie os dados com robocopy ou AWS DataSync. |
| Amazon S3 | `legacyshop-<env>-<conta>-deploy` | artefatos (zip) do CodeDeploy | — | LegacyShop.Web, LegacyShop.Worker, LegacyShop.Importador, LegacyShop.Relatorios | export ArtifactsBucketName; usado pelo workflow/esteira | — |

### 2.7 Rede e integrações

| Tipo | Alvo | O que configurar | Usado por |
|---|---|---|---|
| Rede on-premises | erp.interno | rota pela VPN/Direct Connect, regra de firewall de saída da VPC e resolução de nome (Route 53 Resolver outbound) para o host interno | LegacyShop.Worker |
| Rede on-premises | srv-sql01 | rota pela VPN/Direct Connect, regra de firewall de saída da VPC e resolução de nome (Route 53 Resolver outbound) para o host interno | LegacyShop.Web, LegacyShop.Worker, LegacyShop.Importador, LegacyShop.Relatorios |
| AWS Managed Microsoft AD (ou AD Connector) | controladores de domínio on-premises | AWS Managed Microsoft AD (ou trust com o AD on-premises) para Integrated Security/FSx/autenticação Windows | LegacyShop.Worker |
| Amazon SES | SMTP smtp.exemplo.com.br | verificar o domínio remetente, sair do sandbox, criar credenciais SMTP (Secrets Manager) e apontar o host SMTP da aplicação para email-smtp.&lt;região&gt;.amazonaws.com:587 | LegacyShop.Importador, LegacyShop.Relatorios, LegacyShop.Web, LegacyShop.Worker |
| Microsoft Graph (fora da AWS) / Exchange via VPN | leitura de caixa postal (Microsoft.Exchange.WebServices) | registrar aplicação no Entra ID com permissão Mail.Read na caixa postal e guardar client id/secret no Secrets Manager (EWS será bloqueado) | LegacyShop.Importador |

### 2.8 Esteira

| Arquivo | Chave | Valor gerado | O que fazer |
|---|---|---|---|
| `.iupipes.yml` | `deploy.aws.dev.account` | `123456789012` | conta AWS de dev |
| `.iupipes.yml` | `deploy.aws.hom.account` | `123456789012` | conta AWS de hom |
| `.iupipes.yml` | `deploy.aws.prod.account` | `123456789012` | conta AWS de prod |
| `.iupipes.yml` | `project.language` | `dotnet-framework` | confirme com a plataforma o valor para .NET Framework (build com MSBuild em agente Windows) |
| `.iupipes.yml` | `infra.cloudformation.aws-owner-contact-email / aws-tech-team-email` | `po@empresa.com.br / time@empresa.com.br` | e-mails do PO e do time técnico |
| `.iupipes.yml` | `security.fortify.sigla / sigla-app` | `SIGLA / SIGLA-APP` | sigla da aplicação no catálogo |
| `.iupipes.yml` | `quality.sonar.gate-name` | `''` | quality gate do Sonar da squad |
| `infra/<env>/parameters*.json` | `DevToolsAccount` | `123456789012` | conta das ferramentas da esteira (ECR/artefatos) |
| `tests/testspec-dev.yml, testspec-hom.yml` | `phases.build.commands` | `echo 'Testes executados manualmente'` | automatize: smoke test da URL publicada e testes via vstest |
| `.github/workflows/deploy.yml` | `secrets.AWS_DEPLOY_ROLE_ARN / vars` | `a definir` | só se a esteira da plataforma não for usada: role OIDC do GitHub e conta por ambiente |

### 2.9 Checklist de implantação

**1. Preparar**

- [ ] Preencher os placeholders dos parâmetros de cada ambiente — infra/dev\|hom\|prod/parameters*.json: VPC, subnets, roles, listener/cluster, conta da esteira, tags; revisar as URLs/e-mails por ambiente
- [ ] Preencher .iupipes.yml e abrir o repositório na esteira — contas por ambiente, sigla, e-mails, language; subir app/src, infra/, tests/ e .iupipes.yml (nunca _secrets/)

**2. Dados e segredos**

- [ ] Criar a stack de dados: ./deploy.sh dev → legacyshop-dev-data — RDS, bucket, FSx, nomes dos segredos
- [ ] Colocar os valores nos segredos — _secrets/&lt;projeto&gt;/create-secrets.sh para os extraídos; JSON manual em legacyshop/&lt;projeto&gt;/config; nada fica em PREENCHER
- [ ] Migrar o banco e gravar a connection string nova no segredo — restore .bak/DMS, usuário da aplicação, Encrypt/TrustServerCertificate conforme o engine
- [ ] Copiar as pastas de rede para o FSx — robocopy/DataSync; mesmos nomes de pasta
- [ ] Configurar o File Gateway sobre o bucket e montar nas instâncias

**3. Serviços**

- [ ] Criar as stacks de serviço (./deploy.sh &lt;env&gt;) e publicar pela esteira — user data instala IIS/.NET 4.8.1/agentes; CodeDeploy publica o zip com codedeploy/&lt;micro&gt;/
- [ ] AWS Managed Microsoft AD (ou AD Connector): AWS Managed Microsoft AD (ou trust com o AD on-premises) para Integrated Security/FSx/autenticação Windows
- [ ] Amazon SES: verificar o domínio remetente, sair do sandbox, criar credenciais SMTP (Secrets Manager) e apontar o host SMTP da aplicação para email-smtp.&lt;região&gt;.amazonaws.com:587
- [ ] Microsoft Graph (fora da AWS) / Exchange via VPN: registrar aplicação no Entra ID com permissão Mail.Read na caixa postal e guardar client id/secret no Secrets Manager (EWS será bloqueado)
- [ ] Liberar a rede para os hosts on-premises ainda usados — erp.interno, srv-sql01

**4. Validar**

- [ ] Health check do target group em 200 e validate-service.ps1 passando — ajustar HealthCheckPath/porta se preciso; conferir logs no CloudWatch/Datadog
- [ ] Rodar os testes de aceitação (TAAC) em dev e hom — tests/testspec-*.yml; automatizar o echo
- [ ] Testar o comportamento, não só a subida — autenticação, sessão, uploads, agendamentos, envio de e-mail, integrações

**5. Cutover**

- [ ] Apontar o DNS para o ALB/NLB compartilhado — registro Route 53 para ListenerRuleHost
- [ ] Rotacionar as credenciais que estavam em texto claro no repositório antigo

**6. Produção**

- [ ] Capacidade e imagem: DesiredCapacity ≥ 2 nas web, AMI do EC2 Image Builder, ingress só do ALB
- [ ] Alarmes: ajustar o FilterPattern ao formato de log e o tópico SNS — {{resolve:ssm:/org/member/workload_local_sns_arn:1}}
- [ ] Backups e retenção — AWS Backup/retenção do RDS, lifecycle dos buckets, retenção dos log groups

## 3. Arquitetura alvo (AWS)

LegacyShop tem 6 projeto(s): 1 aplicação web, 1 biblioteca, 1 Windows Service, 1 projeto de testes, 2 consoles. LegacyShop.Web (web): 3 controller(s) MVC e 8 view(s); 2 controller(s) de API; login por Forms Authentication; sessão em memória; upload de arquivos; 1 arquivo(s) Web Forms; acessa SQL Server (LegacyShop), SQL Server (Relatorios); envia e-mail por SMTP; grava arquivos locais; inclui LegacyShop.Core → EC2 Windows. LegacyShop.Worker (serviço): Windows Service; loop com timer; agendador (Quartz); acessa SQL Server (LegacyShop); envia e-mail por SMTP; consome serviços WCF/SOAP; inclui LegacyShop.Core → EC2 Windows. LegacyShop.Importador (console): lê caixa de e-mail (Microsoft.Exchange.WebServices); processa planilhas/CSV; acessa SQL Server (LegacyShop); envia e-mail por SMTP; grava arquivos locais; inclui LegacyShop.Core → EC2 Windows. LegacyShop.Relatorios (console): VB.NET (não convertido); acessa SQL Server (Relatorios); envia e-mail por SMTP; grava arquivos locais → EC2 Windows. Destino escolhido: .NET Framework 4.8.1 sem alteração de código (lift-and-shift para EC2 Windows); a coluna 'Alternativas' mostra a hospedagem após a migração para .NET 10.

### Hospedagem recomendada

| Projeto | Tipo | Hospedagem | Por quê | Pré-requisitos | Alternativas |
|---|---|---|---|---|---|
| **LegacyShop.Web** | Web (MVC/Web API) | **EC2 Windows** | • O código permanece em .NET Framework 4.8.1 (--target framework): só roda em Windows com IIS. EC2 Windows Server 2022 em Auto Scaling group atrás de um Application Load Balancer, AMI padronizada (EC2 Image Builder) e deploy pelo CodeDeploy.<br>• Web Forms roda no IIS sem alteração; a reescrita só é necessária na migração futura para .NET 10.<br>• Após migrar o código para .NET 10 a recomendação passa a ser ECS Fargate (Linux) + ALB: Aplicação ASP.NET Core sem dependências Windows: container Linux no ECS Fargate atrás de um Application Load Balancer é o padrão de menor operação para o portfólio (mesmo pipeline, mesma observabilidade para todas as aplicações). | • Endpoint de health check para o ALB (página/action que responda 200 sem autenticação, ex.: /health.aspx ou /health); informe o caminho no parâmetro HealthCheckPath da stack.<br>• machineKey explícita no web.config, igual em todas as instâncias (Forms Authentication/ViewState com mais de uma instância atrás do ALB).<br>• Sessão InProc não é compartilhada entre instâncias: habilite stickiness no target group (menos resiliente) ou mova a sessão para SQL Server (aspnet_regsql) / Redis (provider).<br>• Pastas locais (C:\..., App_Data) ficam no disco EBS da instância e se perdem na troca por Auto Scaling: aponte para FSx/File Gateway ou aceite instância única.<br>• Senhas fora do repositório: o script after-install.ps1 do CodeDeploy lê o segredo &lt;app&gt;/&lt;projeto&gt;/config no Secrets Manager e grava no web.config/app.config da instância; rotacione as credenciais que estavam em texto claro.<br>• SMTP interno → Amazon SES (endpoint SMTP na porta 587, credenciais SMTP no Secrets Manager) ou relay pela VPN.<br>• Logs: CloudWatch agent (instalado no user data) coleta Event Log e arquivos de log para o CloudWatch Logs.<br>• Fuso horário da instância definido no user data (tzutil) para manter DateTime.Now como on-premises. | • ECS com containers Windows (imagem mcr.microsoft.com/dotnet/framework/aspnet:4.8.1): deploy por imagem e o mesmo cluster das demais aplicações, mas imagens de 5-10 GB e sem Fargate Spot.<br>• Migrar o código para .NET 10 (rode o Migrator sem --target framework): ECS Fargate (Linux) + ALB; alternativas nessa trilha: AWS App Runner — mesma imagem Docker com menos configuração (sem cluster/ALB próprios); menos controle de rede e sem Spot. Bom para aplicações internas pequenas. / AWS Elastic Beanstalk (.NET on Linux) — sem Docker, deploy do publish; plataforma mais antiga, menos padronizável que ECS. |
| **LegacyShop.Core** | Biblioteca | **não publicável (biblioteca/testes)** | • Biblioteca: é empacotada dentro dos projetos que a referenciam; as dependências dela foram consideradas na recomendação deles. | — | — |
| **LegacyShop.Worker** | Windows Service | **EC2 Windows** | • O código permanece em .NET Framework 4.8.1 (--target framework): Windows Service instalado em uma instância EC2 Windows (Auto Scaling group de tamanho 1 para auto-recuperação), deploy pelo CodeDeploy.<br>• Após migrar o código para .NET 10 a recomendação passa a ser ECS Fargate (tarefa agendada via EventBridge Scheduler): Processo periódico (timer/agendador): Amazon EventBridge Scheduler dispara uma tarefa ECS Fargate (RunTask) no horário; a task termina ao concluir e não há custo entre execuções. | • Pastas locais (C:\..., App_Data) ficam no disco EBS da instância e se perdem na troca por Auto Scaling: aponte para FSx/File Gateway ou aceite instância única.<br>• Integrated Security exige instâncias ingressadas no domínio (AWS Managed Microsoft AD ou AD Connector + VPN) e RDS com autenticação Windows; alternativa simples: autenticação SQL + Secrets Manager.<br>• SMTP interno → Amazon SES (endpoint SMTP na porta 587, credenciais SMTP no Secrets Manager) ou relay pela VPN.<br>• Logs: CloudWatch agent (instalado no user data) coleta Event Log e arquivos de log para o CloudWatch Logs.<br>• Fuso horário da instância definido no user data (tzutil) para manter DateTime.Now como on-premises. | • ECS com containers Windows (imagem mcr.microsoft.com/dotnet/framework/runtime:4.8.1) com EventBridge Scheduler: deploy por imagem; exige adaptar o serviço para rodar como processo de console.<br>• Migrar o código para .NET 10 (rode o Migrator sem --target framework): ECS Fargate (tarefa agendada via EventBridge Scheduler); alternativas nessa trilha: ECS Fargate como serviço contínuo (BackgroundService + PeriodicTimer) — mais simples de portar, paga 24x7 e precisa de lock distribuído se escalar. / AWS Lambda agendado pelo EventBridge — se a execução durar menos de 15 min e couber em 10 GB de memória. |
| **LegacyShop.Tests** | Testes | **não publicável (biblioteca/testes)** | • Projeto de testes: roda no pipeline de CI (AWS CodeBuild ou GitHub Actions), não é publicado. | — | — |
| **LegacyShop.Importador** | Console | **EC2 Windows** | • O código permanece em .NET Framework 4.8.1 (--target framework): console agendado pelo Agendador de Tarefas do Windows em uma instância EC2 Windows (Auto Scaling group de tamanho 1), deploy pelo CodeDeploy.<br>• Após migrar o código para .NET 10 a recomendação passa a ser ECS Fargate (tarefa agendada via EventBridge Scheduler): Automação de retaguarda executada por agendamento: Amazon EventBridge Scheduler dispara uma tarefa ECS Fargate (RunTask) com o Main() como está; a task termina ao concluir e não há custo entre execuções. | • Compartilhamentos \\servidor\pasta → Amazon FSx for Windows File Server (SMB nativo, exige Active Directory) ou AWS Storage Gateway (File Gateway: SMB sobre S3), sem mudar o código; copie os dados com robocopy/DataSync.<br>• Senhas fora do repositório: o script after-install.ps1 do CodeDeploy lê o segredo &lt;app&gt;/&lt;projeto&gt;/config no Secrets Manager e grava no web.config/app.config da instância; rotacione as credenciais que estavam em texto claro.<br>• EWS está sendo desligado no Exchange Online: a leitura da caixa postal deve migrar para Microsoft Graph (o SDK roda no .NET Framework 4.8.1); se o Exchange for on-premises, liberar o acesso pela VPN.<br>• SMTP interno → Amazon SES (endpoint SMTP na porta 587, credenciais SMTP no Secrets Manager) ou relay pela VPN.<br>• Fuso horário da instância definido no user data (tzutil) para manter DateTime.Now como on-premises. | • ECS com containers Windows (imagem mcr.microsoft.com/dotnet/framework/runtime:4.8.1) com EventBridge Scheduler: deploy por imagem; exige adaptar o serviço para rodar como processo de console.<br>• Migrar o código para .NET 10 (rode o Migrator sem --target framework): ECS Fargate (tarefa agendada via EventBridge Scheduler); alternativas nessa trilha: ECS Fargate como serviço contínuo (BackgroundService + PeriodicTimer) — mais simples de portar, paga 24x7 e precisa de lock distribuído se escalar. / AWS Lambda agendado pelo EventBridge — se a execução durar menos de 15 min e couber em 10 GB de memória. |
| **LegacyShop.Relatorios** | Console | **EC2 Windows** ⚠ exige Windows | • O código permanece em .NET Framework 4.8.1 (--target framework): console agendado pelo Agendador de Tarefas do Windows em uma instância EC2 Windows (Auto Scaling group de tamanho 1), deploy pelo CodeDeploy.<br>• Dependências Windows (componentes COM (COMReference Microsoft.Office.Interop.Excel); Registro do Windows (DLL Legacy.Impressao); P/Invoke em DLLs do Windows (DLL Legacy.Impressao); Office Interop (GeradorRelatorio.vb:4)) continuam atendidas na instância.<br>• Após migrar o código para .NET 10 a recomendação passa a ser ECS com containers Windows: Depende de componentes exclusivos do Windows: componentes COM (COMReference Microsoft.Office.Interop.Excel); Registro do Windows (DLL Legacy.Impressao); P/Invoke em DLLs do Windows (DLL Legacy.Impressao); Office Interop (GeradorRelatorio.vb:4). Container Windows no ECS (ou EC2 Windows se precisar de sessão interativa). | • Compartilhamentos \\servidor\pasta → Amazon FSx for Windows File Server (SMB nativo, exige Active Directory) ou AWS Storage Gateway (File Gateway: SMB sobre S3), sem mudar o código; copie os dados com robocopy/DataSync.<br>• Senhas fora do repositório: o script after-install.ps1 do CodeDeploy lê o segredo &lt;app&gt;/&lt;projeto&gt;/config no Secrets Manager e grava no web.config/app.config da instância; rotacione as credenciais que estavam em texto claro.<br>• SMTP interno → Amazon SES (endpoint SMTP na porta 587, credenciais SMTP no Secrets Manager) ou relay pela VPN.<br>• Logs: CloudWatch agent (instalado no user data) coleta Event Log e arquivos de log para o CloudWatch Logs.<br>• Fuso horário da instância definido no user data (tzutil) para manter DateTime.Now como on-premises. | • ECS com containers Windows (imagem mcr.microsoft.com/dotnet/framework/runtime:4.8.1) com EventBridge Scheduler: deploy por imagem; exige adaptar o serviço para rodar como processo de console.<br>• Migrar o código para .NET 10 (rode o Migrator sem --target framework): ECS com containers Windows; alternativas nessa trilha: ECS Fargate (Linux) após remover as dependências Windows. |

### Serviços

| Serviço AWS | Papel | Substitui | Por quê | Usado por | Necessidade |
|---|---|---|---|---|---|
| **Amazon VPC (subnets privadas, NAT Gateway, VPC endpoints)** | Rede | rede do datacenter | Instâncias em subnets privadas; endpoints para S3, SSM, Secrets Manager e CloudWatch evitam NAT para serviços AWS. _NAT Gateway é custo fixo: use endpoints._ |  | obrigatório |
| **Application Load Balancer + AWS Certificate Manager** | Entrada HTTP/HTTPS, TLS, health checks e roteamento por host | IIS bindings / certificados no servidor | Termina TLS com certificados do ACM, distribui entre instâncias e tira do rodízio as que falham no health check. Um ALB serve várias aplicações por host header. _Habilite access logs no S3 e, para aplicações públicas, AWS WAF. Sticky sessions só se a sessão InProc não for movida._ | LegacyShop.Web | obrigatório |
| **Amazon EC2 (Windows Server 2022) + Auto Scaling** | Servidores para IIS, Windows Services e consoles agendados (.NET Framework 4.8.1) | servidores Windows on-premises | O código em .NET Framework roda sem alteração; o Auto Scaling group recria a instância em caso de falha e, para a web, escala por CPU. AMI padronizada com IIS/.NET 4.8.1/agentes (EC2 Image Builder) ou user data gerado. _Instâncias em subnets privadas, acesso por Session Manager (sem RDP exposto), patching pelo Patch Manager. Licença Windows inclusa no preço da instância._ | LegacyShop.Importador, LegacyShop.Relatorios, LegacyShop.Web, LegacyShop.Worker | obrigatório |
| **AWS Managed Microsoft AD (ou AD Connector)** | Domínio para as instâncias: autenticação Windows, Integrated Security e FSx | controladores de domínio on-premises | A aplicação depende de identidade Windows (autenticação/Integrated Security): as instâncias precisam ingressar num domínio com trust para o AD corporativo. | LegacyShop.Worker | obrigatório |
| **Amazon RDS for SQL Server** | Banco de dados (LegacyShop, Relatorios); 3 tabela(s) acessadas: Produto, PedidoImportado, Vendas — detalhes na seção 'Dados acessados' | SQL Server em (LocalDb)\MSSQLLocalDB, srv-sql01 | Mesmo engine, backups automáticos, Multi-AZ e patching gerenciado; restore nativo a partir de .bak no S3 para migrar os dados. _Licença inclusa (Standard/Enterprise/Web/Express). Integrated Security exige AWS Managed Microsoft AD; prefira autenticação SQL + Secrets Manager. Para reduzir licenciamento a longo prazo: Aurora PostgreSQL com Babelfish._ | LegacyShop.Web, LegacyShop.Worker | obrigatório |
| **Amazon FSx for Windows File Server** | Compartilhamentos SMB (pastas de rede) montados nas instâncias | pastas de rede: arquivos | Mesmo caminho UNC, sem mudar o código; Multi-AZ, backups e cotas; integrado ao Active Directory. Dados copiados com robocopy ou AWS DataSync. _Exige um Active Directory (AWS Managed Microsoft AD ou o AD corporativo via VPN); informe ActiveDirectoryId na stack de storage._ | LegacyShop.Importador, LegacyShop.Relatorios, LegacyShop.Web, LegacyShop.Worker | obrigatório |
| **Amazon SES** | Envio de e-mail | SMTP smtp.exemplo.com.br | Endpoint SMTP compatível (porta 587, credenciais SMTP) sem mudar o código; métricas de bounce; DKIM gerenciado. _Verifique domínio e saia do sandbox antes do go-live._ | LegacyShop.Importador, LegacyShop.Relatorios, LegacyShop.Web, LegacyShop.Worker | obrigatório |
| **AWS Secrets Manager** | Senhas de banco, caixa postal, SMTP e chaves de API | senhas no web.config/app.config | O script after-install.ps1 do CodeDeploy lê o segredo &lt;app&gt;/&lt;projeto&gt;/config (JSON chave→valor) e grava no config da instância: nada no repositório nem na AMI. _Instance profile restrito ao prefixo &lt;app&gt;/; rotação automática para a senha do RDS._ | LegacyShop.Importador, LegacyShop.Relatorios, LegacyShop.Web, LegacyShop.Worker | obrigatório |
| **AWS Systems Manager (Session Manager, Patch Manager, Parameter Store)** | Acesso administrativo sem RDP, patching automático e parâmetros por ambiente | RDP / WSUS / configurações por servidor | Sem portas abertas para administração; janelas de patch gerenciadas; o CloudWatch agent é instalado e configurado pelo SSM. | LegacyShop.Importador, LegacyShop.Relatorios, LegacyShop.Web, LegacyShop.Worker | obrigatório |
| **Amazon CloudWatch (agent, Logs, Alarms)** | Event Log, arquivos de log, métricas e alarmes | Event Viewer / arquivos de log no servidor | O agent envia Event Log de aplicação e arquivos de log para o CloudWatch Logs; alarmes de CPU, 5xx do ALB e instâncias sem saúde notificam por SNS. _Defina retenção dos log groups._ | LegacyShop.Importador, LegacyShop.Relatorios, LegacyShop.Web, LegacyShop.Worker | obrigatório |
| **AWS Site-to-Site VPN ou Direct Connect + Route 53 Resolver** | Conectividade com a rede on-premises | acesso direto na LAN a srv-sql01, erp.interno | Integrações internas (ERP, serviços, Exchange) continuam on-premises; o DNS interno precisa ser resolvível da VPC. _Direct Connect se o volume for alto; VPN para começar._ | LegacyShop.Importador, LegacyShop.Relatorios, LegacyShop.Web, LegacyShop.Worker | obrigatório |
| **AWS CodeDeploy + GitHub Actions (runner Windows com MSBuild)** | Build com MSBuild, pacote no S3 e deploy in-place nas instâncias (para IIS site, serviço ou tarefa agendada) | publicação manual / MSDeploy / copiar e colar | Pipeline padronizado para todas as aplicações .NET Framework do portfólio; rollback por deployment anterior; os scripts PowerShell gerados (infra/codedeploy/) configuram IIS, serviço e segredos na instância. | LegacyShop.Importador, LegacyShop.Relatorios, LegacyShop.Web, LegacyShop.Worker | obrigatório |
| **Amazon Route 53** | DNS | DNS interno / registros apontando para servidores | Registros alias para o ALB; roteamento ponderado permite cutover gradual e rollback. |  | recomendado |
| **Amazon S3 (+ AWS Storage Gateway File Gateway)** | Arquivos no S3 sem alterar o código: o File Gateway expõe o bucket como compartilhamento SMB | pastas de rede / locais | Os arquivos ficam duráveis e baratos no S3, prontos para a etapa .NET 10 (eventos S3 → Lambda); o gateway roda como instância EC2 ou appliance on-premises. _Bucket privado, versionado e criptografado (criado pela stack de storage). Para quem grava direto via SDK não é preciso gateway._ | LegacyShop.Importador, LegacyShop.Relatorios, LegacyShop.Web, LegacyShop.Worker | recomendado |
| **Microsoft Graph (fora da AWS) / Exchange via VPN** | Leitura da caixa postal pela automação | leitura de caixa postal (Microsoft.Exchange.WebServices) | Sem mudar a arquitetura a instância continua consultando a caixa; se o Exchange for on-premises, pela VPN. EWS está sendo desligado no Exchange Online: planeje a troca por Microsoft Graph (há SDK para .NET Framework). _Na trilha .NET 10 a leitura vira SES recebimento ou Lambda agendada com Graph._ | LegacyShop.Importador | recomendado |
| **AWS Backup** | Backups de EBS, FSx e RDS por política | backup dos servidores | Política única de retenção para discos, compartilhamentos e banco; restauração pontual. |  | recomendado |

### Diagrama

```mermaid
flowchart LR
    users([Usuários])
    alb[Application Load Balancer]
    subgraph aws[AWS - VPC]
        direction LR
        subgraph ec2[EC2 Windows - Auto Scaling]
            LegacyShop_Web["LegacyShop.Web<br/>IIS (.NET Framework 4.8.1)"]
            LegacyShop_Worker["LegacyShop.Worker<br/>Windows Service (.NET Framework 4.8.1)"]
            LegacyShop_Importador["LegacyShop.Importador<br/>tarefa agendada (.NET Framework 4.8.1)"]
            LegacyShop_Relatorios["LegacyShop.Relatorios<br/>tarefa agendada (.NET Framework 4.8.1)"]
        end
        rds_sqlserver[(RDS SQL Server)]
        fsx[(FSx for Windows)]
        ad[(Managed AD)]
    end
    s3[S3 via File Gateway]
    ses[SES]
    secrets[Secrets Manager]
    ssm[Parameter Store]
    cloudwatch[CloudWatch]
    cicd[CodeDeploy]
    graph[Exchange / Graph]
    subgraph onprem[On-premises]
        op_erp_interno[erp.interno]
    end
    users --> alb
    alb --> LegacyShop_Web
    LegacyShop_Web --> rds_sqlserver
    LegacyShop_Web -- SMB --> fsx
    LegacyShop_Web -.-> s3
    LegacyShop_Web --> ses
    LegacyShop_Web -.-> ad
    LegacyShop_Worker --> rds_sqlserver
    LegacyShop_Worker -- SMB --> fsx
    LegacyShop_Worker -.-> s3
    LegacyShop_Worker --> ses
    LegacyShop_Worker -.-> ad
    LegacyShop_Worker -. VPN .-> op_erp_interno
    LegacyShop_Importador --> rds_sqlserver
    LegacyShop_Importador -- SMB --> fsx
    LegacyShop_Importador -.-> s3
    LegacyShop_Importador --> ses
    LegacyShop_Importador -- EWS/Graph --> graph
    LegacyShop_Importador -.-> ad
    LegacyShop_Relatorios --> rds_sqlserver
    LegacyShop_Relatorios -- SMB --> fsx
    LegacyShop_Relatorios -.-> s3
    LegacyShop_Relatorios --> ses
    cicd -- deploy --> LegacyShop_Web
    secrets -.-> LegacyShop_Web
    LegacyShop_Web -. logs .-> cloudwatch
```

### Plano de migração

1. Fundação: VPC com subnets privadas e endpoints (stack 00-network), Secrets Manager com os segredos de cada projeto, bucket de artefatos do CodeDeploy, diretório (Managed AD/AD Connector), VPN/Direct Connect e Route 53 Resolver para o DNS interno.
2. Dados: RDS (stack 10-data), restore nativo do .bak via S3 ou AWS DMS com replicação contínua; usuário da aplicação com autenticação SQL e connection string no Secrets Manager; Encrypt/TLS e collation validados.
3. Arquivos: FSx for Windows (stack 20-storage) com os mesmos nomes de compartilhamento; cópia inicial com robocopy/DataSync e cópia final no cutover; ou File Gateway sobre o S3.
4. Aplicação: compilar a saída do Migrator (4.8.1) com MSBuild, publicar pelo workflow gerado (CodeDeploy) na stack 30-compute; machineKey/segredos/fuso definidos pelos scripts; health check no ALB.
5. Cutover: homologação com dados reais, teste de carga para dimensionar instâncias, Route 53 com roteamento ponderado (10% → 100%), alarmes e runbook de rollback (voltar o peso do DNS).
6. Próxima etapa (modernização): rodar o Migrator sem --target framework para gerar a versão .NET 10 e mover cada aplicação para ECS Fargate/Lambda (coluna 'Alternativas'), eliminando a licença Windows e os servidores.

### Riscos

- Lift-and-shift mantém o custo de Windows (licença no preço da instância) e servidores para administrar; o ganho é prazo e risco baixo, não custo. Planeje a migração para .NET 10 como etapa seguinte.
- Atualização 4.x → 4.8.1 é compatível em binário, mas há mudanças de comportamento acumuladas (TLS 1.2 por padrão, criptografia, Regex, WPF/WinForms DPI): teste os fluxos de integração antes do cutover.
- Sessão InProc com mais de uma instância atrás do ALB: usuários perdem a sessão a cada deploy/scale-in. Stickiness mitiga; sessão em SQL/Redis resolve.
- Caminhos locais (C:\..., App_Data) no disco da instância: com Auto Scaling os arquivos somem na troca da instância. Mova para FSx/File Gateway ou fixe uma instância.
- Integrated Security: sem domínio (Managed AD) a conexão falha; decida entre ingressar as instâncias no domínio ou trocar para autenticação SQL antes do primeiro deploy.
- EWS no Exchange Online está em desligamento: a automação de caixa postal pode parar independentemente da migração; priorize a troca por Microsoft Graph.
- Senhas em texto claro nos configs copiados: bloqueie no pipeline (git-secrets/trufflehog), use os scripts do CodeDeploy para injetar do Secrets Manager e rotacione o que já vazou.
- Consoles agendados em instância única: sem lock distribuído, não rode a mesma tarefa em duas instâncias; o Auto Scaling group de tamanho 1 apenas recria a instância.

### Custo

- EC2 Windows: licença inclusa (~2x o Linux equivalente); comece com t3.medium/t3.large por aplicação web e 1 instância por serviço; Savings Plans após estabilizar.
- RDS for SQL Server: a licença inclusa domina o custo (Standard ≈ 2-3x um RDS PostgreSQL); vários bancos cabem numa instância; Express é gratuito em licença até 10 GB por banco.
- FSx for Windows: mínimo de 32 GB SSD e throughput de 32 MB/s; Multi-AZ dobra o custo — use Single-AZ em homologação.
- CodeDeploy em EC2 não tem custo; GitHub Actions com runner Windows consome minutos a 2x o Linux.
- NAT Gateway tem custo fixo por hora + por GB; VPC endpoints para S3/SSM/Secrets Manager/CloudWatch evitam a maior parte do tráfego.
- O custo cai de verdade na etapa seguinte (.NET 10 em Fargate Linux/Lambda): mantenha a migração de código no roadmap.

## 4. Dados acessados (bancos, tabelas e campos)

Bancos, tabelas, procedures e campos que o código acessa, extraídos de SQL em literais C#/VB e arquivos .sql, leitores ADO.NET, chamadas Dapper, modelos EF6/EF Core e EDMX. O banco é resolvido pelas connection strings (nome três partes no SQL > nome da connection string citada no código > banco único do projeto ou de quem o hospeda). Análise estática: tabelas montadas dinamicamente e colunas lidas por índice não aparecem; confira com o DBA antes de migrar os dados.

### LegacyShop (SQL Server) — 2 tabela(s), 0 procedure(s)

| Tabela / procedure | Campos acessados | Operações | Acesso | Projetos | Onde |
|---|---|---|---|---|---|
| **PedidoImportado** | Cliente, Data, Numero, Valor | INSERT | ADO.NET | LegacyShop.Importador | `ImportadorDePedidos.cs:74` |
| **Produto** | Categoria, Descricao, Destaque, Id, Nome, Preco | DELETE, EF (escrita), EF (leitura), SELECT | arquivo .sql, Dapper, EF6 | LegacyShop.Core | `Services/ProdutoService.cs:81; Sql/ConsultaDestaques.sql; Data/ShopContext.cs:13` |

### Relatorios (SQL Server) — 1 tabela(s), 0 procedure(s)

| Tabela / procedure | Campos acessados | Operações | Acesso | Projetos | Onde |
|---|---|---|---|---|---|
| **Vendas** | *, Mes, Produto, Valor | SELECT | ADO.NET | LegacyShop.Relatorios | `GeradorRelatorio.vb:16` |

## 5. Modernização (39)

Sugestões que não bloqueiam a compilação: bibliotecas que passaram a ser pagas ou foram descontinuadas, código C# que compila mas muda de comportamento no .NET 10/Linux, e adaptações para a AWS. Detalhes completos em `modernization.csv` e na aba Modernização do `inventory.xlsx`.

| Projeto | Tipo | Impacto | Esforço | Item | Proposta | Serviço AWS |
|---|---|---|---|---|---|---|
| LegacyShop.Web | Cloud (AWS) | Alto | Baixo | **[MOD-ARCH-DATAPROTECTION] Chaves do Data Protection compartilhadas entre instâncias**<br>_Cookies de autenticação, antiforgery e sessão são protegidos pelo Data Protection; cada container gera chaves próprias (o equivalente ao machineKey do web.config), então um cookie emitido por uma task é inválido na outra e todos são invalidados a cada deploy._<br>`Web.config, authentication mode=Forms` | Persista o anel de chaves fora do container: pacote Amazon.AspNetCore.DataProtection.SSM (PersistKeysToAWSSystemsManager) ou S3 + KMS (AspNetCore.DataProtection.Aws.S3), e defina SetApplicationName. | AWS Systems Manager Parameter Store |
| LegacyShop.Worker | Cloud (AWS) | Alto | Baixo | **[MOD-ARCH-DB-INTEGRATED] Connection string 'DefaultConnection' usa Integrated Security**<br>_Autenticação Windows no SQL Server exige que o container esteja no domínio (gMSA em Windows ou Kerberos em Linux). O Amazon RDS for SQL Server aceita Windows Auth apenas com AWS Managed Microsoft AD._<br>`srv-sql01/LegacyShop` | Troque para autenticação SQL com a senha no Secrets Manager (rotação automática) e Encrypt=True com o certificado do RDS; alternativa: AWS Managed Microsoft AD + gMSA. | Amazon RDS for SQL Server / AWS Secrets Manager |
| LegacyShop.Web | Cloud (AWS) | Alto | Médio | **[MOD-ARCH-FILES] Arquivos no disco local/compartilhamento → Amazon S3** (×4)<br>_O disco de um container é efêmero e não é compartilhado entre instâncias: uploads, exportações, App_Data e pastas de rede (UNC) deixam de funcionar ou somem no próximo deploy._<br>`Controllers/ProdutosController.cs:38, Controllers/ProdutosController.cs:37, Handlers/Thumbnail.ashx.cs:9, Controllers/ProdutosController.cs:33` | Grave e leia via AWSSDK.S3 (um bucket por aplicação, prefixos por tipo); uploads grandes direto do navegador com URL pré-assinada; arquivos servidos via CloudFront. Para cache temporário, use /tmp (até 20 GB de storage efêmero no Fargate). | Amazon S3 |
| LegacyShop.Worker | Cloud (AWS) | Alto | Médio | **[MOD-ARCH-FILES] Arquivos no disco local/compartilhamento → Amazon S3** (×2)<br>_O disco de um container é efêmero e não é compartilhado entre instâncias: uploads, exportações, App_Data e pastas de rede (UNC) deixam de funcionar ou somem no próximo deploy._<br>`Properties/Settings.Designer.cs:15, App.config, C:\Exportacao, C:\Exportacao\Estoque` | Grave e leia via AWSSDK.S3 (um bucket por aplicação, prefixos por tipo); uploads grandes direto do navegador com URL pré-assinada; arquivos servidos via CloudFront. Para cache temporário, use /tmp (até 20 GB de storage efêmero no Fargate). | Amazon S3 |
| LegacyShop.Importador | Cloud (AWS) | Alto | Médio | **[MOD-ARCH-FILES] Arquivos no disco local/compartilhamento → Amazon S3** (×3)<br>_O disco de um container é efêmero e não é compartilhado entre instâncias: uploads, exportações, App_Data e pastas de rede (UNC) deixam de funcionar ou somem no próximo deploy. Compartilhamentos SMB (\\servidor\pasta) não são acessíveis do Fargate sem Amazon FSx/EFS._<br>`ImportadorDePedidos.cs:40, App.config, arquivos, C:\Importacao\Processados` | Grave e leia via AWSSDK.S3 (um bucket por aplicação, prefixos por tipo); uploads grandes direto do navegador com URL pré-assinada; arquivos servidos via CloudFront. Se for lift-and-shift, monte um volume Amazon EFS na task do ECS (ou FSx for Windows File Server quando outros sistemas Windows também usam a pasta). | Amazon S3 |
| LegacyShop.Relatorios | Cloud (AWS) | Alto | Médio | **[MOD-ARCH-FILES] Arquivos no disco local/compartilhamento → Amazon S3** (×2)<br>_O disco de um container é efêmero e não é compartilhado entre instâncias: uploads, exportações, App_Data e pastas de rede (UNC) deixam de funcionar ou somem no próximo deploy. Compartilhamentos SMB (\\servidor\pasta) não são acessíveis do Fargate sem Amazon FSx/EFS._<br>`GeradorRelatorio.vb:28, GeradorRelatorio.vb:12, arquivos` | Grave e leia via AWSSDK.S3 (um bucket por aplicação, prefixos por tipo); uploads grandes direto do navegador com URL pré-assinada; arquivos servidos via CloudFront. Se for lift-and-shift, monte um volume Amazon EFS na task do ECS (ou FSx for Windows File Server quando outros sistemas Windows também usam a pasta). | Amazon S3 |
| LegacyShop.Web | Cloud (AWS) | Alto | Médio | **[MOD-ARCH-HYBRID] Dependências on-premises (rede interna)**<br>_A aplicação fala com hosts da rede interna (srv-sql01). Da AWS eles só são alcançáveis com conectividade híbrida, e a latência (ida e volta por VPN) multiplica o tempo de chamadas em loop._<br>`srv-sql01` | Provisione Site-to-Site VPN ou Direct Connect e resolução de nomes (Route 53 Resolver outbound endpoints para o DNS interno). Bancos devem ir para o RDS na mesma região; integrações (ERP, WCF) ficam via VPN com timeouts e retry explícitos. Remova hosts fixos do código: configuração/variáveis de ambiente. | AWS Site-to-Site VPN / Direct Connect |
| LegacyShop.Worker | Cloud (AWS) | Alto | Médio | **[MOD-ARCH-HYBRID] Dependências on-premises (rede interna)**<br>_A aplicação fala com hosts da rede interna (erp.interno, srv-sql01). Da AWS eles só são alcançáveis com conectividade híbrida, e a latência (ida e volta por VPN) multiplica o tempo de chamadas em loop._<br>`erp.interno, srv-sql01` | Provisione Site-to-Site VPN ou Direct Connect e resolução de nomes (Route 53 Resolver outbound endpoints para o DNS interno). Bancos devem ir para o RDS na mesma região; integrações (ERP, WCF) ficam via VPN com timeouts e retry explícitos. Remova hosts fixos do código: configuração/variáveis de ambiente. | AWS Site-to-Site VPN / Direct Connect |
| LegacyShop.Importador | Cloud (AWS) | Alto | Médio | **[MOD-ARCH-HYBRID] Dependências on-premises (rede interna)**<br>_A aplicação fala com hosts da rede interna (srv-sql01). Da AWS eles só são alcançáveis com conectividade híbrida, e a latência (ida e volta por VPN) multiplica o tempo de chamadas em loop._<br>`srv-sql01` | Provisione Site-to-Site VPN ou Direct Connect e resolução de nomes (Route 53 Resolver outbound endpoints para o DNS interno). Bancos devem ir para o RDS na mesma região; integrações (ERP, WCF) ficam via VPN com timeouts e retry explícitos. Remova hosts fixos do código: configuração/variáveis de ambiente. | AWS Site-to-Site VPN / Direct Connect |
| LegacyShop.Relatorios | Cloud (AWS) | Alto | Médio | **[MOD-ARCH-HYBRID] Dependências on-premises (rede interna)**<br>_A aplicação fala com hosts da rede interna (srv-sql01). Da AWS eles só são alcançáveis com conectividade híbrida, e a latência (ida e volta por VPN) multiplica o tempo de chamadas em loop._<br>`srv-sql01` | Provisione Site-to-Site VPN ou Direct Connect e resolução de nomes (Route 53 Resolver outbound endpoints para o DNS interno). Bancos devem ir para o RDS na mesma região; integrações (ERP, WCF) ficam via VPN com timeouts e retry explícitos. Remova hosts fixos do código: configuração/variáveis de ambiente. | AWS Site-to-Site VPN / Direct Connect |
| LegacyShop.Importador | Cloud (AWS) | Alto | Médio | **[MOD-ARCH-MAILBOX] Leitura de caixa de e-mail → Amazon SES (recebimento) ou Microsoft Graph** (×4)<br>_A automação faz polling de uma caixa postal via EWS, que a Microsoft está desligando no Exchange Online (bloqueio a partir de outubro de 2026). Em container/Lambda isso exige credenciais da caixa (no Secrets Manager), tratamento de duplicidade e um agendamento; e-mails com anexos grandes consomem memória do processo._<br>`ImportadorDePedidos.cs:7, ImportadorDePedidos.cs:48, ImportadorDePedidos.cs:55, Microsoft.Exchange.WebServices` | Preferido: apontar um (sub)domínio para o Amazon SES e receber por regra → S3 → SQS/Lambda: sem polling, sem senha de caixa, anexos já no S3. Se a caixa tiver que ficar no M365: Microsoft Graph (client credentials, Mail.Read com Application Access Policy) chamado por uma Lambda agendada no EventBridge; ou regra de encaminhamento da caixa para o SES. | Amazon SES (recebimento) |
| LegacyShop.Web | Cloud (AWS) | Alto | Médio | **[MOD-ARCH-SESSION] Sessão InProc → cache distribuído (ElastiCache)** (×3)<br>_Com mais de uma task no ECS (ou a cada deploy) a sessão em memória se perde e o usuário é deslogado/perde o carrinho. Sticky sessions no ALB só mascaram o problema._<br>`Controllers/ProdutosController.cs:46, Controllers/ProdutosController.cs:48, Web.config, sessionState mode=InProc` | Program.cs: troque AddDistributedMemoryCache por AddStackExchangeRedisCache apontando para o Amazon ElastiCache (Valkey/Redis OSS, TLS). Objetos em Session[] precisam ser serializados (JSON) — veja os itens WEB003 do inventário. | Amazon ElastiCache |
| LegacyShop.Web | Licença | Alto | Médio | **[MOD-PKG-AUTOMAPPER] AutoMapper exige licença comercial a partir da v15**<br>_Desde a v15 (2025) o AutoMapper é distribuído pela Lucky Penny Software sob licença comercial. A ferramenta manteve a última versão gratuita (14.x), que não receberá correções nem suporte ao .NET futuro._<br>`AutoMapper 6.2.2` | Opções: (1) Mapperly — source generator gratuito (Apache-2.0), sem reflexão e mais rápido; a migração é mecânica (atributos [Mapper]). (2) Mapeamento manual com extension methods, o mais simples para poucos DTOs. (3) Comprar a licença se o volume de perfis for grande. |  |
| LegacyShop.Web | Licença | Alto | Alto | **[MOD-PKG-ITEXTSHARP] iTextSharp 5 é AGPL e está descontinuado**<br>_O iTextSharp 5.5.x só tem licença AGPL (obriga abrir o código) ou comercial, não recebe correções desde 2016 e tem vulnerabilidades conhecidas em XML._<br>`iTextSharp 5.5.13` | Geração: QuestPDF (gratuito para empresas com receita &lt; US$ 1M, API fluente) ou PdfSharpCore (MIT). Manipulação de PDFs existentes: PdfPig (leitura) / PDFsharp 6 (MIT). Se o volume justificar, iText 9 com licença comercial. |  |
| LegacyShop.Web | Segurança | Alto | Baixo | **[MOD-SEC-SECRETS] Senhas e chaves em arquivos de configuração → AWS Secrets Manager** (×3)<br>_Credenciais em texto claro (connectionStrings/RelatoriosConnection, network/@password, PagamentoApiKey) foram copiadas para o appsettings.json e acabariam na imagem Docker/repositório._<br>`Web.config, connectionStrings/RelatoriosConnection, network/@password, PagamentoApiKey` | Remova do appsettings.json; injete via Secrets Manager (secrets na task definition do ECS ou Amazon.Extensions.Configuration.SystemsManager no IConfiguration). Para o RDS, prefira autenticação IAM ou rotação automática do Secrets Manager. | AWS Secrets Manager |
| LegacyShop.Importador | Segurança | Alto | Baixo | **[MOD-SEC-SECRETS] Senhas e chaves em arquivos de configuração → AWS Secrets Manager** (×2)<br>_Credenciais em texto claro (CaixaPostalSenha, connectionStrings/DefaultConnection) foram copiadas para o appsettings.json e acabariam na imagem Docker/repositório._<br>`App.config, CaixaPostalSenha, connectionStrings/DefaultConnection` | Remova do appsettings.json; injete via Secrets Manager (secrets na task definition do ECS ou Amazon.Extensions.Configuration.SystemsManager no IConfiguration). Para o RDS, prefira autenticação IAM ou rotação automática do Secrets Manager. | AWS Secrets Manager |
| LegacyShop.Relatorios | Segurança | Alto | Baixo | **[MOD-SEC-SECRETS] Senhas e chaves em arquivos de configuração → AWS Secrets Manager**<br>_Credenciais em texto claro (connectionStrings/Relatorios) foram copiadas para o appsettings.json e acabariam na imagem Docker/repositório._<br>`App.config, connectionStrings/Relatorios` | Remova do appsettings.json; injete via Secrets Manager (secrets na task definition do ECS ou Amazon.Extensions.Configuration.SystemsManager no IConfiguration). Para o RDS, prefira autenticação IAM ou rotação automática do Secrets Manager. | AWS Secrets Manager |
| LegacyShop.Web | Segurança | Alto | Baixo | **[MOD-SEC-SECRETS-CODE] Credenciais embutidas no código-fonte → AWS Secrets Manager** (×2)<br>_Senhas, chaves de API ou tokens aparecem como literais no C# (TokenIntegracaoErp). Vão para o repositório, para a imagem Docker e não podem ser rotacionados sem novo deploy. O Migrator trocou 2 literal(is) por leitura de configuração e moveu os valores para _secrets/ (item CS-SECRET-EXTERNALIZED); o que restou está em CS-CONFIG-SKIPPED._<br>`Helpers/AppConfig.cs:14, Helpers/AppConfig.cs:15, TokenIntegracaoErp` | Leia de IConfiguration (injetado) e alimente via Secrets Manager na task definition do ECS ou Amazon.Extensions.Configuration.SystemsManager; para serviços AWS use a task role (sem access keys). Rotacione as credenciais expostas. | AWS Secrets Manager |
| LegacyShop.Web | Descontinuado | Alto | Alto | **[MOD-ARCH-WEBFORMS] Web Forms → Razor Pages ou Blazor**<br>_ASP.NET Web Forms (1 arquivo(s) .aspx/.ascx/.master) não existe no .NET 10 e não há conversor automático confiável; enquanto não for reescrita, a aplicação depende de IIS/Windows._<br>`Relatorios/Vendas.aspx, página .aspx` | Razor Pages é o caminho mais curto (uma página .cshtml + PageModel por .aspx; ViewState e eventos de servidor viram handlers OnGet/OnPost); Blazor Server se houver muita interatividade. Comece pelas páginas mais usadas e exponha a lógica de negócio como serviços reutilizáveis. Páginas de relatório podem virar exportações (QuestPDF/ClosedXML) em vez de telas. | Amazon ECS (após a reescrita) |
| LegacyShop.Importador | Descontinuado | Alto | Alto | **[MOD-PKG-EWS] EWS Managed API: a Microsoft está desligando o EWS no Exchange Online**<br>_A Microsoft começou a bloquear chamadas EWS no Exchange Online em outubro de 2026 e o pacote não recebe manutenção desde 2015 (só .NET Framework; funciona no .NET 10 via compatibilidade, com NU1701). Automações que leem caixas postais por EWS vão parar de funcionar._<br>`Microsoft.Exchange.WebServices 2.2` | Microsoft Graph (pacote Microsoft.Graph, autenticação de aplicativo com Microsoft.Identity.Client; permissão Mail.Read restrita à caixa por Application Access Policy). Alternativa sem código específico de Microsoft: se a caixa puder ter o domínio apontado para a AWS, receba por Amazon SES (regras de recebimento → S3 → SQS/Lambda) e elimine o polling. | Amazon SES (recebimento) / EventBridge Scheduler |
| LegacyShop.Web | Modernização | Alto | Alto | **[MOD-PKG-EF6] EF6 → EF Core 10**<br>_EF6 6.5 roda no .NET 10, mas está em manutenção mínima: sem novos providers, sem EDMX em projetos SDK-style, sem suporte a Aurora PostgreSQL e sem as otimizações de consulta do EF Core._<br>`EntityFramework 6.2.0` | Migre contexto a contexto: EF Core 10 com Microsoft.EntityFrameworkCore.SqlServer; para EDMX gere as classes com dotnet ef dbcontext scaffold. Isso abre a opção de trocar o banco por Aurora PostgreSQL (Npgsql) e eliminar o licenciamento do SQL Server. | Amazon Aurora PostgreSQL (opcional) |
| LegacyShop.Core | Modernização | Alto | Alto | **[MOD-PKG-EF6] EF6 → EF Core 10**<br>_EF6 6.5 roda no .NET 10, mas está em manutenção mínima: sem novos providers, sem EDMX em projetos SDK-style, sem suporte a Aurora PostgreSQL e sem as otimizações de consulta do EF Core._<br>`EntityFramework 6.2.0` | Migre contexto a contexto: EF Core 10 com Microsoft.EntityFrameworkCore.SqlServer; para EDMX gere as classes com dotnet ef dbcontext scaffold. Isso abre a opção de trocar o banco por Aurora PostgreSQL (Npgsql) e eliminar o licenciamento do SQL Server. | Amazon Aurora PostgreSQL (opcional) |
| LegacyShop.Web | Cloud (AWS) | Médio | Baixo | **[MOD-ARCH-SMTP] Envio de e-mail → Amazon SES**<br>_O servidor SMTP interno fica fora da VPC (ou some com a migração) e SmtpClient está obsoleto (sem suporte a autenticação moderna)._<br>`Web.config, smtp.exemplo.com.br` | Amazon SES: endpoint SMTP (email-smtp.&lt;região&gt;.amazonaws.com, porta 587 STARTTLS) com MailKit, ou API via AWSSDK.SimpleEmailV2. Verifique o domínio remetente (DKIM/SPF) e saia do sandbox do SES antes do go-live. | Amazon SES |
| LegacyShop.Core | Cloud (AWS) | Médio | Baixo | **[MOD-ARCH-SMTP] Envio de e-mail → Amazon SES** (×2)<br>_O servidor SMTP interno fica fora da VPC (ou some com a migração) e SmtpClient está obsoleto (sem suporte a autenticação moderna)._<br>`Services/EmailService.cs:2, Services/EmailService.cs:15` | Amazon SES: endpoint SMTP (email-smtp.&lt;região&gt;.amazonaws.com, porta 587 STARTTLS) com MailKit, ou API via AWSSDK.SimpleEmailV2. Verifique o domínio remetente (DKIM/SPF) e saia do sandbox do SES antes do go-live. | Amazon SES |
| LegacyShop.Relatorios | Cloud (AWS) | Médio | Baixo | **[MOD-ARCH-SMTP] Envio de e-mail → Amazon SES** (×2)<br>_O servidor SMTP interno fica fora da VPC (ou some com a migração) e SmtpClient está obsoleto (sem suporte a autenticação moderna)._<br>`GeradorRelatorio.vb:34, App.config, smtp.exemplo.com.br` | Amazon SES: endpoint SMTP (email-smtp.&lt;região&gt;.amazonaws.com, porta 587 STARTTLS) com MailKit, ou API via AWSSDK.SimpleEmailV2. Verifique o domínio remetente (DKIM/SPF) e saia do sandbox do SES antes do go-live. | Amazon SES |
| LegacyShop.Web | Cloud (AWS) | Médio | Baixo | **[MOD-PKG-LOG4NET] log4net: direcione os logs para stdout/CloudWatch**<br>_Appenders de arquivo e EventLog não fazem sentido em containers (disco efêmero, sem Event Log). O log4net também não tem logging estruturado nem integração nativa com ILogger._<br>`log4net 2.0.8` | Mínimo: trocar os appenders por ConsoleAppender (o ECS envia stdout ao CloudWatch Logs). Recomendado: Microsoft.Extensions.Logging com AddJsonConsole() ou Serilog (Serilog.AspNetCore + Serilog.Formatting.Compact) para consultas no CloudWatch Logs Insights. | Amazon CloudWatch Logs |
| LegacyShop.Worker | Cloud (AWS) | Médio | Baixo | **[MOD-PKG-LOG4NET] log4net: direcione os logs para stdout/CloudWatch**<br>_Appenders de arquivo e EventLog não fazem sentido em containers (disco efêmero, sem Event Log). O log4net também não tem logging estruturado nem integração nativa com ILogger._<br>`log4net 2.0.8` | Mínimo: trocar os appenders por ConsoleAppender (o ECS envia stdout ao CloudWatch Logs). Recomendado: Microsoft.Extensions.Logging com AddJsonConsole() ou Serilog (Serilog.AspNetCore + Serilog.Formatting.Compact) para consultas no CloudWatch Logs Insights. | Amazon CloudWatch Logs |
| LegacyShop.Web | Cloud (AWS) | Médio | Médio | **[MOD-ARCH-LONG-REQUESTS] Requisições longas (executionTimeout alto)**<br>_executionTimeout=300s: o ALB encerra conexões ociosas em 60 s por padrão e o Kestrel não tem executionTimeout._<br>`Web.config, executionTimeout=300s` | Para processamento demorado use o padrão assíncrono: a action enfileira (SQS) e devolve 202 + URL de status; um worker ECS/Lambda processa. Se precisar, aumente o idle timeout do ALB (até 4000 s). | Amazon SQS |
| LegacyShop.Web | Cloud (AWS) | Médio | Médio | **[MOD-ARCH-UPLOADS] Uploads grandes passando pela aplicação** (×2)<br>_Limites configurados (maxAllowedContentLength=50 MB, maxRequestLength=20480 KB) indicam uploads grandes. Passar pelo ALB e pelo container consome memória/CPU da task e esbarra em timeouts._<br>`Web.config, maxAllowedContentLength=50 MB, maxRequestLength=20480 KB` | Envie direto do navegador para o S3 com URL pré-assinada (PUT) ou multipart upload, e notifique a aplicação (evento S3 → SQS/Lambda). | Amazon S3 |
| LegacyShop.Worker | Cloud (AWS) | Médio | Médio | **[MOD-PKG-QUARTZ] Quartz em containers**<br>_Com mais de uma instância o Quartz exige clustering (store ADO.NET no RDS); sem isso os jobs rodam em duplicidade. Quartz 2.x → 3.x mudou a API para assíncrona._<br>`Quartz 2.6.2` | Opção A: Quartz 3 com AdoJobStore clusterizado no RDS. Opção B (recomendada para o portfólio): EventBridge Scheduler → tarefa ECS (RunTask) ou Lambda por job, com retry e histórico nativos. | Amazon EventBridge Scheduler |
| LegacyShop.Web | Cloud (AWS) | Médio | Alto | **[MOD-COST-SQLSERVER] Custo de licença do SQL Server no RDS**<br>_No RDS o SQL Server é cobrado com licença inclusa (Standard/Enterprise), frequentemente o maior item da fatura para aplicações pequenas._<br>`LegacyShop, Relatorios` | Após estabilizar: avalie Amazon Aurora PostgreSQL com Babelfish (fala TDS/T-SQL, reduz reescrita) ou migração via EF Core + Npgsql; use AWS SCT/DMS para converter schema e dados. Para bancos pequenos, RDS SQL Server Express (gratuito em licença) pode bastar. | Amazon Aurora PostgreSQL (Babelfish) |
| LegacyShop.Core | Cloud (AWS) | Médio | Alto | **[MOD-COST-SQLSERVER] Custo de licença do SQL Server no RDS**<br>_No RDS o SQL Server é cobrado com licença inclusa (Standard/Enterprise), frequentemente o maior item da fatura para aplicações pequenas._<br>`LegacyShop` | Após estabilizar: avalie Amazon Aurora PostgreSQL com Babelfish (fala TDS/T-SQL, reduz reescrita) ou migração via EF Core + Npgsql; use AWS SCT/DMS para converter schema e dados. Para bancos pequenos, RDS SQL Server Express (gratuito em licença) pode bastar. | Amazon Aurora PostgreSQL (Babelfish) |
| LegacyShop.Core | Cloud (AWS) | Médio | Alto | **[MOD-PKG-IDENTITY2] ASP.NET Identity 2 → ASP.NET Core Identity ou Amazon Cognito**<br>_Identity 2 depende de OWIN/System.Web (já bloqueante no inventário). A migração para ASP.NET Core Identity mantém usuários no RDS; o Cognito terceiriza login, MFA, recuperação de senha e federação._<br>`Microsoft.AspNet.Identity.Core 2.2.3` | Se a aplicação é interna e já tem AD: Cognito com federação SAML/OIDC ao Entra ID/AD. Se tem base própria de usuários: ASP.NET Core Identity no RDS (hashes do Identity 2 continuam válidos) e, opcionalmente, migração em lote para o Cognito depois. | Amazon Cognito |
| LegacyShop.Web | Modernização | Médio | Alto | **[MOD-ARCH-IDENTITY] Login próprio (Forms/Membership) → Amazon Cognito ou ASP.NET Core Identity**<br>_A ferramenta converteu Forms Authentication em cookie authentication, mas a validação de usuário/senha, recuperação de senha e MFA continuam por conta da aplicação._<br>`Web.config, authentication mode=Forms` | Amazon Cognito (hosted UI, MFA, federação) via AddOpenIdConnect, ou ASP.NET Core Identity no RDS se a base de usuários precisar ficar na aplicação. | Amazon Cognito |
| LegacyShop.Worker | Descontinuado | Baixo | Baixo | **[MOD-PKG-COMMONLOGGING] Common.Logging está descontinuado**<br>_Abstração de logging sem manutenção desde 2019._<br>`Common.Logging 3.4.1, Common.Logging.Core` | Use Microsoft.Extensions.Logging (ILogger&lt;T&gt;), que já é a abstração padrão do .NET; adaptadores existem para log4net, NLog e Serilog. |  |
| LegacyShop.Web | Modernização | Baixo | Baixo | **[MOD-ARCH-HARDCODED-URL] URLs fixas no código** (×2)<br>_Endereços embutidos (https://erp.exemplo.com.br, https://loja.exemplo.com.br) impedem ter ambientes distintos (dev/homolog/prod) com a mesma imagem. O Migrator externalizou 2 valor(es) para configuração (item CS-CONFIG-EXTERNALIZED), que viram parâmetros por ambiente na infraestrutura._<br>`App_Start/WebApiConfig.cs:10, Helpers/AppConfig.cs:10, https://erp.exemplo.com.br, https://loja.exemplo.com.br` | Leve para IConfiguration (appsettings por ambiente / variáveis de ambiente / Parameter Store) e injete com IOptions&lt;T&gt;. | AWS Systems Manager Parameter Store |
| LegacyShop.Web | Modernização | Baixo | Baixo | **[MOD-PKG-WEBAPICLIENT] ReadAsAsync/PostAsJsonAsync → System.Net.Http.Json**<br>_O pacote só existe por compatibilidade; o BCL traz ReadFromJsonAsync/PostAsJsonAsync nativos (System.Text.Json)._<br>`Microsoft.AspNet.WebApi.Client 5.2.7` | Troque os usings e remova o pacote quando sair do Newtonsoft. |  |
| LegacyShop.Web | Modernização | Baixo | Médio | **[MOD-PKG-NEWTONSOFT] Newtonsoft.Json → System.Text.Json (etapa posterior)**<br>_A ferramenta manteve Newtonsoft no Web API para preservar o contrato JSON dos clientes. System.Text.Json é 2-5x mais rápido e já é o padrão do ASP.NET Core._<br>`Newtonsoft.Json 12.0.2` | Depois de estabilizar, migre por controller/DTO: remova AddNewtonsoftJson, configure PropertyNamingPolicy = null se precisar manter PascalCase e troque [JsonProperty] por [JsonPropertyName]. |  |
| LegacyShop.Core | Modernização | Baixo | Médio | **[MOD-PKG-NEWTONSOFT] Newtonsoft.Json → System.Text.Json (etapa posterior)**<br>_A ferramenta manteve Newtonsoft no Web API para preservar o contrato JSON dos clientes. System.Text.Json é 2-5x mais rápido e já é o padrão do ASP.NET Core._<br>`Newtonsoft.Json 12.0.2` | Depois de estabilizar, migre por controller/DTO: remova AddNewtonsoftJson, configure PropertyNamingPolicy = null se precisar manter PascalCase e troque [JsonProperty] por [JsonPropertyName]. |  |

## 6. Inventário da migração

O que a ferramenta resolveu e o que ainda exige ação, por projeto. Bloqueante impede compilar ou funcionar; Atenção compila mas pode mudar de comportamento; Automático já foi feito (registro para auditoria).

### LegacyShop.Web

Web (MVC/Web API) · v4.7.2 → v4.8.1 · pasta `LegacyShop.Web`

#### Pontos de atenção (1)

- **[CFG-FX-SECRETS] Credenciais permanecem no web.config/app.config** — `Web.config`
  - No destino .NET Framework o arquivo de configuração não é convertido, então as senhas continuam no arquivo copiado.
  - *Sugestão:* Use os scripts do CodeDeploy gerados em infra/codedeploy/ (after-install.ps1 lê o segredo &lt;app&gt;/&lt;projeto&gt;/config no Secrets Manager e grava no config da instância) e remova as senhas do repositório.

#### Resolvido automaticamente (5)

- 2 valor(es) fixos no código viraram configuração: Urls:ErpProtocoloUrl, Emails:EmailSuporte — URLs e e-mails fixos em literais (Helpers/AppConfig.cs:10, Helpers/AppConfig.cs:11) foram trocados por leitura de configuração; a chave e o valor atual estão no app/web.config e viram parâmetros da infraestrutura (um valor por ambiente). Campos const passaram a static readonly.
- 2 credencial(is) fixas no código movidas para _secrets/: Credenciais:TokenIntegracaoErp, Credenciais:ConexaoRelatoriosAntiga — Os literais (Helpers/AppConfig.cs:14, Helpers/AppConfig.cs:15) viraram leitura de configuração com marcador '&lt;secret: nome&gt;'; os valores reais e os scripts do Secrets Manager estão em _secrets/LegacyShop.Web/ (fora do git e do Docker).
- Web.config: runtime apontado para 4.8.1 (compilation 4.7.2→4.8.1, httpRuntime 4.7.2→4.8.1) — supportedRuntime/httpRuntime/compilation targetFramework atualizados; o restante do arquivo foi mantido.
- 2 segredo(s) retirados do appsettings*.json — Marcador '&lt;secret: nome&gt;' no lugar de AppSettings:Credenciais:TokenIntegracaoErp, AppSettings:Credenciais:ConexaoRelatoriosAntiga. Valores, scripts para o Secrets Manager, bloco da task definition e user-secrets em _secrets/LegacyShop.Web/ (ignorado pelo git e pelo Docker).
- LegacyShop.Web.csproj: v4.7.2 → v4.8.1 (código não alterado) — Apenas o TargetFrameworkVersion foi atualizado; packages.config, referências e arquivos permanecem como no original. O .NET Framework 4.8.1 é compatível em binário com as versões 4.x anteriores.

#### Informativo (2)

- **[WEB-WEBFORMS-KEPT] Web Forms mantido (1 arquivo(s) .aspx/.ascx/.master)**
  - No destino .NET Framework 4.8.1 as páginas continuam funcionando no IIS sem alteração.
  - *Sugestão:* A reescrita (Razor Pages/Blazor) só é necessária na migração futura para .NET 10.
- **[PRJ-DLL] DLL local Legacy.Barcode: System.Web (ASP.NET clássico)** — `../lib/Legacy.Barcode.dll`
  - Compilada para .NETFramework,Version=v4.8; usa APIs que não existem no .NET 10: System.Web (ASP.NET clássico). Nada muda no destino .NET Framework; é o que impede esta DLL de rodar em .NET 10/Linux depois.
  - *Sugestão:* Para a modernização, recompile a DLL para netstandard2.0 a partir do fonte ou substitua-a; a hospedagem proposta já considera essas dependências.

### LegacyShop.Core

Biblioteca · v4.6.1 → v4.8.1 · pasta `LegacyShop.Core`

#### Resolvido automaticamente (1)

- LegacyShop.Core.csproj: v4.6.1 → v4.8.1 (código não alterado) — Apenas o TargetFrameworkVersion foi atualizado; packages.config, referências e arquivos permanecem como no original. O .NET Framework 4.8.1 é compatível em binário com as versões 4.x anteriores.

### LegacyShop.Worker

Windows Service · v4.6.1 → v4.8.1 · pasta `LegacyShop.Worker`

#### Resolvido automaticamente (2)

- App.config: runtime apontado para 4.8.1 (supportedRuntime v4.6.1→v4.8.1) — supportedRuntime/httpRuntime/compilation targetFramework atualizados; o restante do arquivo foi mantido.
- LegacyShop.Worker.csproj: v4.6.1 → v4.8.1 (código não alterado) — Apenas o TargetFrameworkVersion foi atualizado; packages.config, referências e arquivos permanecem como no original. O .NET Framework 4.8.1 é compatível em binário com as versões 4.x anteriores.

### LegacyShop.Tests

Testes · v4.6.1 → v4.8.1 · pasta `LegacyShop.Tests`

#### Resolvido automaticamente (1)

- LegacyShop.Tests.csproj: v4.6.1 → v4.8.1 (código não alterado) — Apenas o TargetFrameworkVersion foi atualizado; packages.config, referências e arquivos permanecem como no original. O .NET Framework 4.8.1 é compatível em binário com as versões 4.x anteriores.

### LegacyShop.Importador

Console · v4.5 → v4.8.1 · pasta `LegacyShop.Importador`

#### Pontos de atenção (1)

- **[CFG-FX-SECRETS] Credenciais permanecem no web.config/app.config** — `App.config`
  - No destino .NET Framework o arquivo de configuração não é convertido, então as senhas continuam no arquivo copiado.
  - *Sugestão:* Use os scripts do CodeDeploy gerados em infra/codedeploy/ (after-install.ps1 lê o segredo &lt;app&gt;/&lt;projeto&gt;/config no Secrets Manager e grava no config da instância) e remova as senhas do repositório.

#### Resolvido automaticamente (2)

- App.config: runtime apontado para 4.8.1 (supportedRuntime v4.5→v4.8.1) — supportedRuntime/httpRuntime/compilation targetFramework atualizados; o restante do arquivo foi mantido.
- LegacyShop.Importador.csproj: v4.5 → v4.8.1 (código não alterado) — Apenas o TargetFrameworkVersion foi atualizado; packages.config, referências e arquivos permanecem como no original. O .NET Framework 4.8.1 é compatível em binário com as versões 4.x anteriores.

### LegacyShop.Relatorios

Console · VB.NET · v4.5 → v4.8.1 · pasta `LegacyShop.Relatorios`

#### Pontos de atenção (1)

- **[CFG-FX-SECRETS] Credenciais permanecem no web.config/app.config** — `App.config`
  - No destino .NET Framework o arquivo de configuração não é convertido, então as senhas continuam no arquivo copiado.
  - *Sugestão:* Use os scripts do CodeDeploy gerados em infra/codedeploy/ (after-install.ps1 lê o segredo &lt;app&gt;/&lt;projeto&gt;/config no Secrets Manager e grava no config da instância) e remova as senhas do repositório.

#### Resolvido automaticamente (1)

- LegacyShop.Relatorios.vbproj: v4.5 → v4.8.1 (código não alterado) — Apenas o TargetFrameworkVersion foi atualizado; packages.config, referências e arquivos permanecem como no original. O .NET Framework 4.8.1 é compatível em binário com as versões 4.x anteriores.

#### Informativo (1)

- **[PRJ-DLL] DLL local Legacy.Impressao: Registro do Windows (Microsoft.Win32.Registry, Microsoft.Win32.RegistryKey); P/Invoke em winspool.drv...** — `../lib/Legacy.Impressao.dll`
  - Compilada para .NETFramework,Version=v4.8; usa APIs que só funcionam em Windows: Registro do Windows (Microsoft.Win32.Registry, Microsoft.Win32.RegistryKey); P/Invoke em winspool.drv; Event Log (System.Diagnostics.EventLog, System.Diagnostics.EventLogEntryType); usa APIs que mudam de comportamento no .NET 10/Linux: BinaryFormatter (desligado por padrão no .NET 9+); Encoding.GetEncoding (code pages exigem CodePagesEncodingProvider); precisa de pacotes no projeto migrado (adicionados): System.Configuration.ConfigurationManager (lê app.config, não appsettings.json). Nada muda no destino .NET Framework; é o que impede esta DLL de rodar em .NET 10/Linux depois.
  - *Sugestão:* Para a modernização, recompile a DLL para netstandard2.0 a partir do fonte ou substitua-a; a hospedagem proposta já considera essas dependências.

