# ADR-0027 — Identidade própria com JWT e PIN em dispositivo compartilhado

- **Status:** Aceito
- **Data:** 2026-10-27
- **Origem:** Lacuna em RF-001, RNF-003, §5.2 ("OIDC / OAuth 2.0 com JWT")
- **Relacionados:** ADR-0004, ADR-0018, ADR-0026, ADR-0028

## Contexto

O épico cita "OIDC/OAuth 2.0 com JWT" sem escolher um provedor. Na operação real, a cozinha e o balcão costumam usar **um aparelho compartilhado**, e um login completo a cada troca de funcionário faz a equipe usar uma conta só, o que destrói a rastreabilidade (RN-001.5).

## Decisão

1. **ASP.NET Core Identity** como base de usuários (hash de senha, bloqueio por tentativas), com emissão de tokens pela própria API:
   - **access token JWT** de 15 min, com claims `sub`, `tenant_id`, `employee_id`, `device_id`, `security_stamp`;
   - **refresh token** opaco de 30 dias, **rotativo** a cada uso, gravado como hash, por dispositivo e revogável.
2. **Login** por telefone (E.164) ou e-mail + senha. E-mail é obrigatório só para o proprietário (recuperação de senha). Funcionários sem e-mail recuperam a senha por um código emitido pelo proprietário (ADR-0018).
3. **Modo dispositivo compartilhado:**
   - Quem tem `devices.manage` registra o aparelho como compartilhado do tenant.
   - Funcionários definem um **PIN de 4–6 dígitos** (hash), válido **só** nesse tenant e em aparelhos compartilhados registrados.
   - A troca de usuário é: escolher o nome e digitar o PIN. O resultado é um token do funcionário com `device_id`.
   - 5 erros de PIN bloqueiam o PIN do funcionário por 15 min. O app volta à tela de seleção após inatividade configurável.
4. **Revogação imediata:** desativar o funcionário ou mudar perfis troca o `security_stamp` do vínculo. Um filtro de autenticação compara o stamp com um cache de 30 s, revogando o acesso em até 30 s mesmo com access token válido.
5. **No app:** tokens em `expo-secure-store`, nunca em AsyncStorage ou SQLite.
6. A troca de tenant segue o ADR-0026 (`/auth/switch-tenant`).

## Alternativas consideradas

- **Provedor gerenciado (Auth0, Firebase, Keycloak):** custo por usuário ativo, e o PIN por dispositivo não é suportado de forma nativa.
- **OIDC completo com OpenIddict:** desnecessário sem clientes de terceiros. Pode ser adotado no EP-002 se houver integrações externas.

## Consequências

- O código de segurança fica sob responsabilidade do time: precisa de testes dedicados (rotação, reuso de refresh token revoga a família, bloqueio).
- Login social fica fora do MVP.
