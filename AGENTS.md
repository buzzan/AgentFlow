# AgentFlow 개발 지침

## WinUI 작업

- WinUI 3 앱 구조, C# 구현, Windows App SDK 설정, 빌드 및 실행 검증 작업에는 `$winui-app`을 사용한다.
- 화면 설계나 XAML 변경 전에는 `$winui:winui-design`도 사용한다. 컨트롤과 레이아웃을 고를 때 실제 WinUI 샘플을 참고하고, 새 API는 AgentFlow 프로젝트가 참조하는 Windows App SDK 버전에서 확인한다.
- 필요한 스킬이 현재 세션에 없으면 아래 설치 방법을 안내한다.

## 애플리케이션 개요

AgentFlow는 Windows에서 프로젝트별 Claude Code 에이전트를 매일 지정한 현지 시각에 실행하는 데스크톱 앱이다. 사용자는 프로젝트, Claude CLI 경로, 에이전트, 실행 시각과 작업 지시를 지정한다. 예약은 Windows 작업 스케줄러에 등록되며, 등록한 Windows 계정이 로그인되어 있으면 AgentFlow 창을 닫아도 실행할 수 있다.

## 화면 설계

- **Schedules**는 활성 예약 수, 다음 실행, 최근 실행 실패를 요약한다. 목록은 전체·활성·일시 중지 필터와 행별 토글을 제공한다. 선택한 예약의 로그를 열거나 앞으로의 실행을 취소할 수 있다.
- **New schedule**은 프로젝트와 CLI 경로를 선택하고, 프로젝트의 `.claude/agents`에서 찾은 에이전트를 고르거나 이름을 직접 입력하는 대화상자다. 입력 오류는 대화상자 안에 표시해 사용자가 수정할 수 있게 한다.
- **Run history**는 로그가 있는 예약의 최신 로그를 보여준다. 취소한 예약의 로그도 이 화면에서 열 수 있다.
- 목록은 가상화 가능한 `ListView`와 XAML 행 템플릿을 사용한다. 화면 변경 시 최소 창 크기인 740 × 530 DIP에서 주요 동작이 보이는지 확인한다. 테마 리소스와 키보드 탐색을 유지하고, 자동화 대상에는 고정 `AutomationId`를 부여한다.

## C# 프로젝트 구성

[`AgentFlow.csproj`](AgentFlow.csproj)는 `net10.0-windows10.0.19041.0`, `win-x64`를 대상으로 하는 WinUI 3 프로젝트다. 프레임워크 종속 비패키지 앱으로 배포하며, 실행 PC에는 x64 .NET 10 Runtime과 Windows App Runtime 1.8이 필요하다. 프로젝트에서 기본 항목 검색을 끄고 있으므로 새 C# 파일이나 XAML 페이지를 추가할 때는 해당 `Compile` 또는 `Page` 항목도 등록한다.

| 파일 | 역할 |
| --- | --- |
| [App.xaml](App.xaml) | WinUI 공용 리소스와 테마 스타일 |
| [ScheduleCenter.cs](ScheduleCenter.cs) | 앱 시작, 주 창, 탐색, 예약 편집 및 화면 상태 |
| [ScheduleTable.xaml](ScheduleTable.xaml) · [코드 비하인드](ScheduleTable.xaml.cs) | 목록, 행 템플릿, 토글 및 로그 열기 입력 |
| [ScheduleStore.cs](ScheduleStore.cs) | 저장된 예약 조회, 등록, 화면에 표시할 예약 데이터 구성 |
| [Scheduler.cs](Scheduler.cs) | 예약 데이터 저장, Windows 작업 스케줄러 연동, Claude CLI 실행 |

## 구현 흐름

- 일반 실행은 단일 WinUI 창을 연다. 작업 스케줄러가 `AgentFlow.exe --run <예약 ID>`를 호출하면 GUI 없이 저장된 예약을 실행한다. 두 경로가 같은 실행 파일을 사용하므로 시작 코드를 바꿀 때는 둘 다 확인한다.
- WinUI에서 등록한 예약은 매일 Windows 현지 시각을 기준으로 실행한다. 기존 단발 예약도 읽을 수 있어야 하며, 같은 일일 예약이 겹쳐 실행되지 않도록 한다. 예약 데이터, 결과, 로그는 `%LOCALAPPDATA%\ClaudeAgentScheduler`에 저장한다.
- 예약의 활성 상태와 최근 실행 결과를 별도로 다룬다. 일시 중지가 이전 결과를 덮어쓰면 안 되며, 손상된 예약 파일 하나가 정상 예약 표시를 막지 않게 한다. Task Scheduler 조회와 파일 I/O는 UI 스레드 밖에서 처리한다.

## 빌드 및 검증

Windows x64의 .NET SDK 10.0.404에서 저장소 루트를 기준으로 실행한다.

```powershell
dotnet build .\AgentFlow.csproj -c Release
.\build.ps1
.\tests\run.ps1
```

- 빌드 후 `dist\daily-row-toggle\AgentFlow.exe`를 실행해 실제로 AgentFlow 창이 열리고 응답하는지 확인한다. UI 테스트를 시작하기 전에 해당 창을 닫는다.
- 예약 등록·실행만 확인하려면 `-Suite Integration`, 화면 흐름만 확인하려면 `-Suite UI`를 사용한다. 테스트는 가짜 Claude CLI를 사용한다.
- 결과는 `work\test-results\<실행 ID>\test-results.json`에서 확인한다. 화면 변경 시 캡처가 있으면 오류 안내, 최소 창 크기의 레이아웃, 주요 버튼 표시를 검토한다. 실행하지 못한 항목과 실패 원인은 검증 결과에 남긴다.

## Codex 스킬 설치

1. Codex 대화에서 `$skill-installer winui-app`을 입력해 OpenAI의 `winui-app` 스킬을 설치한다. [OpenAI 스킬 소스](https://github.com/openai/skills/tree/main/skills/.curated/winui-app)
2. PowerShell에서 Microsoft의 WinUI 플러그인을 설치한다. 플러그인에 `winui-design`이 포함되어 있다. [Microsoft WinUI 플러그인 설치 안내](https://learn.microsoft.com/en-us/windows/apps/develop/ai-assisted/winui-agent-plugin)

   ```powershell
   codex plugin marketplace add microsoft/WinAppCli
   codex plugin add winui@winappcli
   ```

3. `winui-design`의 샘플 검색과 API 확인에 쓰는 WinApp CLI가 없다면 설치한다. 새 PowerShell에서 `winapp --help`로 확인한다. [Microsoft WinApp CLI 설치 안내](https://learn.microsoft.com/en-us/windows/apps/dev-tools/winapp-cli/#installation)

   ```powershell
   winget install Microsoft.winappcli --source winget
   ```

설치 후 AgentFlow 폴더에서 새 Codex 세션을 열고 `$winui-app`과 `$winui:winui-design`을 사용할 수 있는지 확인한다.
