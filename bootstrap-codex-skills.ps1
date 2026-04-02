$dirs = @(
  ".codex",
  ".codex/skills",
  ".codex/skills/analyze-requirement",
  ".codex/skills/dotnet-feature-implementation",
  ".codex/skills/api-contract-change",
  ".codex/skills/ef-migration-safe",
  ".codex/skills/bugfix-root-cause",
  ".codex/skills/react-admin-ui-task",
  ".codex/skills/identityserver-security-review",
  ".codex/skills/generate-technical-design",
  ".codex/skills/generate-technical-design/templates",
  ".codex/skills/fullstack-enterprise-implementer"
)

$files = @(
  "AGENTS.md",
  ".codex/skills/analyze-requirement/SKILL.md",
  ".codex/skills/analyze-requirement/prompt.md",
  ".codex/skills/analyze-requirement/checklist.md",
  ".codex/skills/dotnet-feature-implementation/SKILL.md",
  ".codex/skills/dotnet-feature-implementation/prompt.md",
  ".codex/skills/dotnet-feature-implementation/checklist.md",
  ".codex/skills/api-contract-change/SKILL.md",
  ".codex/skills/api-contract-change/prompt.md",
  ".codex/skills/api-contract-change/checklist.md",
  ".codex/skills/ef-migration-safe/SKILL.md",
  ".codex/skills/ef-migration-safe/prompt.md",
  ".codex/skills/ef-migration-safe/checklist.md",
  ".codex/skills/bugfix-root-cause/SKILL.md",
  ".codex/skills/bugfix-root-cause/prompt.md",
  ".codex/skills/bugfix-root-cause/checklist.md",
  ".codex/skills/react-admin-ui-task/SKILL.md",
  ".codex/skills/react-admin-ui-task/prompt.md",
  ".codex/skills/react-admin-ui-task/checklist.md",
  ".codex/skills/identityserver-security-review/SKILL.md",
  ".codex/skills/identityserver-security-review/prompt.md",
  ".codex/skills/identityserver-security-review/checklist.md",
  ".codex/skills/generate-technical-design/SKILL.md",
  ".codex/skills/generate-technical-design/prompt.md",
  ".codex/skills/generate-technical-design/templates/design-template.md",
  ".codex/skills/fullstack-enterprise-implementer/SKILL.md",
  ".codex/skills/fullstack-enterprise-implementer/prompt.md"
)

foreach ($dir in $dirs) {
  New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

foreach ($file in $files) {
  if (-not (Test-Path $file)) {
    New-Item -ItemType File -Force -Path $file | Out-Null
  }
}

Write-Host "Codex skills skeleton created successfully."
