# ADR-0037 — Hospedagem Azure Brazil South, GitHub Actions e serviços de apoio

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** Lacuna em §5.2 ("hospedagem gerenciada", "Azure DevOps ou GitHub Actions"), RNF-005
- **Relacionados:** ADR-0003, ADR-0009, ADR-0024, ADR-0035

## Contexto

Dados financeiros de clientes pagantes exigem backup, restauração ponto-a-ponto e disponibilidade que um servidor próprio não garante. A LGPD e a latência favorecem uma região no Brasil.

## Decisão

| Necessidade | Escolha |
|---|---|
| API + SignalR | **Azure Container Apps** (Brazil South), mínimo de **1 réplica sempre ativa** (sem scale-to-zero, por causa dos WebSockets) |
| Banco | **Azure Database for PostgreSQL – Flexible Server** (Brazil South), backup automático com PITR de 14 dias, papéis separados para migrations e aplicação (ADR-0026) |
| Tempo real com mais de 1 réplica | **Azure SignalR Service** (modo default), só quando escalar. Com 1 réplica, SignalR em processo |
| Imagens de produtos | **Cloudflare R2** (S3-compatível, sem custo de egress), URLs públicas por CDN, upload por URL pré-assinada |
| Segredos | **Azure Key Vault** (produção/homologação) + `dotnet user-secrets` (local). Nenhum segredo versionado |
| Observabilidade | OpenTelemetry → **Azure Monitor / Application Insights** |
| Registro de imagens | GitHub Container Registry (ghcr.io) |
| CI/CD backend | **GitHub Actions**: build, testes (unit + integração com Testcontainers + arquitetura), imagem, deploy em homologação automático e em produção por aprovação |
| CI/CD mobile | GitHub Actions disparando **EAS Build/Update** |
| Ambiente local | Docker Compose (PostgreSQL) |

Ambientes: `local`, `homolog` e `prod`, com bancos separados.

## Alternativas consideradas

- **AWS São Paulo:** equivalente técnico. Azure escolhida pela afinidade com o ecossistema .NET.
- **VPS/k3s próprio:** mais barato, mas backup, PITR e alta disponibilidade ficam manuais, um risco inaceitável para dados financeiros.
- **Azure DevOps Pipelines:** sem ganho sobre o GitHub Actions para um repositório no GitHub.

## Consequências

- Custo fixo mensal mínimo (1 réplica + Flexible Server Burstable) precisa entrar no preço da assinatura.
- O procedimento de restauração precisa ser **ensaiado** antes do piloto (RNF-005).
