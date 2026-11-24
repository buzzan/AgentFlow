# Claude Code 에이전트 예약

Windows x64에서 지정한 프로젝트의 Claude Code 에이전트가 매일 같은 현지 시각에 실행되도록 예약합니다.

## 사용 방법

1. 사용할 Windows 계정으로 Claude Code CLI에 로그인합니다. 대상 프로젝트에서 Claude Code를 한 번 열어 폴더 신뢰 설정과 작업에 필요한 도구 권한을 확인합니다.
2. `dist\daily-row-toggle\AgentFlow.exe`를 실행합니다. 예약을 등록하기 전에 배포 폴더 전체를 계속 사용할 위치에 둡니다.
3. 프로젝트 폴더를 선택합니다. CLI가 자동으로 검색되지 않으면 설치된 `claude.exe`를 선택합니다.
4. `.claude\agents`에서 찾은 에이전트를 선택하거나 이름을 직접 입력합니다. **Daily Run Time**과 작업 지시를 입력하고 **Schedule**을 누릅니다.
5. **Refresh**로 상태를 확인하고 **Open Log**로 가장 최근 실행 로그를 봅니다. **Cancel Schedule**로 이후 반복 실행을 취소할 수 있습니다.

예약 시각은 Windows 현지 시간입니다. 실행 시에는 예약을 등록한 Windows 계정이 로그인되어 있어야 합니다. 예약을 놓치면 Windows 작업 스케줄러에서 실행 가능한 시점에 시작할 수 있습니다. 이전 실행이 진행 중이면 새 실행과 겹치지 않습니다. 예약을 취소할 때까지 배포 폴더와 프로젝트 폴더의 위치를 유지하세요.

Claude Code는 프로젝트 폴더를 작업 디렉터리로 사용하고 기본 권한 모드에서 비대화형으로 실행됩니다. 대화형 승인이 필요한 도구는 예약 실행에서 사용이 거부될 수 있습니다. 작업 지시와 실행 로그는 `%LOCALAPPDATA%\ClaudeAgentScheduler`에 저장됩니다.

## 빌드

Windows x64에서 .NET SDK 10.0.404를 사용해 PowerShell로 `.\build.ps1`을 실행합니다. 배포 결과는 `dist\daily-row-toggle`에 생성됩니다.

실행할 PC에는 x64용 .NET 10 Desktop Runtime과 Windows App Runtime 1.8이 필요합니다.

## 테스트

Windows에 로그인한 상태에서 PowerShell로 `.\tests\run.ps1`을 실행합니다. 빌드 요구 사항과 Windows 작업 스케줄러가 필요합니다. Claude Code 설치, 로그인, API 키는 필요 없습니다.

VS Code에서는 저장소 폴더를 열고 명령 팔레트(`Ctrl+Shift+P`)에서 `Tasks: Run Test Task`를 실행합니다. 결과는 통합 터미널에 표시됩니다.

테스트는 임시 폴더에서 가짜 `claude.exe`를 빌드하고, 실행 인수·작업 폴더·유니코드 입력·로그·실패 코드와 예약 등록·켜기·끄기·취소·중복 실행 방지를 검증합니다. Windows 작업 스케줄러가 테스트 예약을 한 번 실행하며, GUI 테스트는 창 시작, 버튼 상태, 새로 고침, 폼에서 예약 등록·선택·취소를 확인합니다. 테스트가 만든 예약과 작업 데이터는 테스트 종료 시 정리합니다.
