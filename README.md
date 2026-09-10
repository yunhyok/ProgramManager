# Program Manager 0.1.0

개인 개발 프로그램을 트레이의 목록에서 실행하고, 내부망 호스트가 게시한 설치 파일과 변경 이력을 다른 컴퓨터에서 확인하는 Windows 프로그램입니다. 하나의 앱에서 **클라이언트** 또는 **호스트 + 클라이언트** 역할을 선택합니다.

## 설치와 첫 실행

| 컴퓨터 | 설치 파일 | 런타임 |
| --- | --- | --- |
| Windows 10/11 64비트 | `ProgramManager-Setup-0.1.0.exe` | .NET 8 포함 |
| Windows 7 SP1 / 8 계열 | `ProgramManager-Setup-0.1.0-win7.exe` | .NET Framework 4.8 사전 설치 필요 |

설치 위치는 기본적으로 `%LOCALAPPDATA%\Programs\Program Manager`입니다. 바탕화면 바로가기는 기본으로 만들지 않으며, 시작 메뉴에서 실행할 수 있습니다. Windows 로그인 시 자동 실행은 설치 옵션 또는 프로그램 옵션에서 선택합니다.

1. 실행 파일(`.exe`) 또는 기존 바로가기(`.lnk`)를 로컬 목록에 등록합니다.
2. 목록에서 프로그램을 선택해 실행합니다. `.lnk`는 Manager 데이터 폴더로 복사되므로 실행을 확인한 뒤 바탕화면 바로가기를 정리할 수 있습니다. 프로그램 실행 파일 자체는 원래 설치 위치를 유지하세요.
3. 창을 닫으면 트레이에 남습니다. 트레이 아이콘을 한 번 클릭해 목록을 다시 열고, 트레이 메뉴의 **종료**로 완전히 종료합니다.

배포 연결과 사용 순서는 [오프라인 도움말](docs/guide.html)에 있습니다. 프로그램에 함께 설치되므로 인터넷 연결 없이 읽을 수 있습니다.

## 다른 컴퓨터에 배포

호스트 옵션을 켠 개발 컴퓨터에서 프로그램 ID, 이름, 설명, 숫자 버전, 변경 내용, 대상 플랫폼과 EXE/MSI 설치 파일을 게시합니다. 같은 프로그램 ID와 버전으로 `win10-x64` 및 `win7` 설치 파일을 각각 등록할 수 있습니다. 클라이언트는 자신의 OS에 맞는 릴리스를 사용합니다.

호스트가 내보낸 연결 코드를 클라이언트 옵션에 붙여 넣습니다. 코드는 호스트 주소, 포트, 인증서 지문과 공유 비밀을 포함합니다. 코드 자체는 문서·스크린샷·공개 저장소에 넣지 마세요. TLS 1.2 연결에서 등록된 인증서 지문을 검사하고 공유 비밀로 접근을 인증합니다.

클라이언트는 목록을 새로 고친 뒤 원하는 설치 또는 업데이트를 직접 선택합니다. `.part` 임시 파일에 다운로드하고 파일 크기와 SHA-256 검증을 통과한 설치 파일을 실행합니다. 설치 종료 코드와 사용자가 확인한 로컬 실행 경로를 확인한 뒤 설치 버전을 기록합니다. 원격 무인 설치는 제공하지 않습니다.

호스트는 사용자 로그인 상태에서 Program Manager가 실행 중일 때만 배포합니다. 기본 TCP 포트는 `45672`이며, 필요하면 사용자가 Windows 방화벽의 개인 네트워크에서 해당 포트를 허용해야 합니다. 공유기 포트 개방이나 인터넷 중계는 포함하지 않습니다.

## 데이터와 실행 옵션

기본 데이터 폴더는 `%LOCALAPPDATA%\ProgramManager`입니다.

| 경로 | 내용 |
| --- | --- |
| `settings.json` | 역할, 로컬 목록, 보호된 연결 정보 |
| `repository\` | 호스트 카탈로그와 릴리스 설치 파일 |
| `identity\` | 보호된 호스트 인증 정보 |
| `downloads\` | 클라이언트가 받은 설치 파일 |
| `shortcuts\` | 원본 위치와 독립적으로 보관한 바로가기 |
| `cache.json` | 마지막으로 확인한 호스트 카탈로그 |

호스트 인증 정보와 클라이언트 공유 비밀은 Windows 사용자 계정으로 보호됩니다. 데이터 폴더를 다른 계정이나 PC에 단순 복사해 연결 정보를 이전할 수 없습니다. 다른 PC는 연결 코드를 다시 등록하세요. 제거 프로그램은 사용자 데이터 폴더를 삭제하지 않습니다.

```powershell
# 창을 띄우지 않고 트레이에서 시작
ProgramManager.exe --tray

# 별도의 데이터 폴더로 실행 (개발 및 분리된 로컬 확인용)
ProgramManager.exe --data-dir "C:\Temp\ProgramManager-check"
```

## 빌드

Windows, .NET SDK 8, Inno Setup 6.3 이상 6.x가 필요합니다. .NET Framework 참조 어셈블리는 NuGet으로 복원합니다. Windows 7용 설치 프로그램 호환성을 위해 Inno Setup 6을 사용합니다.

```powershell
./tools/build.ps1

# 컴파일러가 기본 경로에 없을 때
./tools/build.ps1 -InnoCompiler "C:\Tools\Inno Setup 6\ISCC.exe"
```

스크립트는 두 대상 프레임워크를 빌드하고 콘솔 검사를 실행한 뒤, 다음 위치에 게시 결과와 설치 프로그램을 만듭니다. 게시 출력 폴더 두 곳은 빌드할 때 새로 생성합니다.

| 출력 | 위치 |
| --- | --- |
| Windows 10/11 실행 파일 및 런타임 | `artifacts/publish/win10-x64/` |
| Windows 7용 실행 파일 및 의존 파일 | `artifacts/publish/win7/` |
| 두 설치 파일 및 `SHA256SUMS.txt` | `artifacts/installers/` |

`.github/workflows/build.yml`은 같은 빌드와 검사를 실행하고 설치 파일을 Actions 아티팩트로 보관합니다. 릴리스 게시 권한이나 자동 GitHub Release 단계는 없습니다. 현재 원격 저장소가 구성되지 않았으므로 원격 CI 실행 및 릴리스 게시 여부와 로컬 빌드 결과를 구분해야 합니다.

## 검증 범위와 제한

자동 검사는 카탈로그·플랫폼 처리, 인증된 호스트/클라이언트 연결, 거부 처리, 파일 무결성과 .NET 8 ↔ .NET Framework 4.8 양방향 통신을 다룹니다. 실제 검사 통과 여부는 빌드 로그로 확인하세요. Windows 7 실기기와 두 번째 물리 PC에서의 설치·통신은 별도 확인이 필요합니다. 최신 Windows에서 `net48`을 실행한 결과만으로 Windows 7 호환성을 확정하지 않습니다.

0.1.0의 빌드·설치·실제 파일 전송·화면 검사 결과는 [검증 기록](docs/verification.md)에 있습니다.

설치 프로그램의 실제 설치 위치는 제품마다 달라 사용자가 실행 경로를 확인합니다. Manager 목록에서 제거해도 대상 프로그램 자체를 삭제하지 않습니다. 자동 롤백, 호스트 자동 검색, Windows 서비스, 인터넷 배포와 원격 무인 설치는 현재 범위에 포함하지 않습니다.

## IntraDrop 통합 방향

현재 IntraDrop 1.7.0의 `TrayApplicationContext`, 사용자별 JSON 설정, `AutoStart.Apply`를 참고했습니다. `PeerRegistry.TryConfirmVerified`와 `PeerRefreshCoordinator.RefreshAsync`가 지키는 **인증된 장치 ID를 확인한 뒤 IP를 변경한다**는 규칙은 향후 호스트 검색에도 유지합니다.

프로그램 카탈로그와 다운로드는 `src/ProgramManager.Core`에 두고 WinForms UI에서 사용합니다. 이후 IntraDrop과 통합할 때 이 C# 코어를 함께 사용하면 됩니다. 기존 IntraDrop의 파일 보내기 프로토콜은 수신 폴더로 파일을 밀어 넣는 방식이므로 카탈로그 조회·릴리스 선택·설치 상태 기능을 직접 대체하지 않습니다.

런타임 설치 판별은 [Inno Setup의 IsDotNetInstalled](https://jrsoftware.org/ishelp/topic_isxfunc_isdotnetinstalled.htm)를 사용합니다. .NET Framework 버전 확인 기준은 [Microsoft 문서](https://learn.microsoft.com/en-us/dotnet/framework/install/how-to-determine-which-versions-are-installed)에 있습니다.
