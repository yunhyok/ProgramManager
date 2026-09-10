# Program Manager 0.1.1 UI/UX 검증 기록

검증일: 2026-09-10. Windows 10 Pro 64비트(10.0.19045), 실제 화면 배율 250%(240 DPI), .NET SDK 8.0.425 및 .NET Framework 4.8.

## 원인과 수정

0.1.0은 폼의 설계 기준 DPI가 빠져 있고 제목·검색줄·표 높이가 고정되어, 250%에서 커진 글자가 작은 프레임 밖으로 잘렸습니다. 이전 화면 검사는 DPI 인식이 없는 실행 파일로 수행해 이 문제를 놓쳤습니다. 기존 UI 코드를 현재 화면 배율로 다시 실행해 잘림을 재현했습니다.

- 모든 폼에 96 DPI 설계 기준을 지정하고, 컨트롤 구성이 끝난 뒤 Windows Forms 자동 크기 조정을 적용했습니다.
- 제목·안내·검색·도구 모음은 내용에 맞게 높이를 계산하고 표 머리글·행은 글꼴에 맞게 조정합니다.
- 기본 창은 화면 작업 영역에 맞춥니다. 버튼이 많은 호스트 도구 모음은 좁은 창에서 줄바꿈합니다.
- 등록·배포·설정·연결 코드 창의 하단 버튼을 입력 영역의 스크롤과 분리했습니다.
- 빈 목록에서는 실행·편집·제거·설치 버튼을 비활성화합니다. 다른 Windows용 배포본을 조회할 때 설치와 기존 설치 연결도 비활성화합니다.
- 설정에서 이 PC의 배포 주소와 연결된 원격 호스트를 구분하고, 클라이언트 역할에서는 이 PC의 호스트 설정을 비활성화합니다.
- .NET Framework에서 중복 확대되던 탭의 불필요한 최소 높이를 제거했습니다.

설계 기준 DPI와 실제 DPI를 비교해 컨트롤을 확대하는 동작은 [Microsoft Windows Forms 자동 크기 조정 문서](https://learn.microsoft.com/en-us/dotnet/desktop/winforms/forms/autoscale)에 설명되어 있습니다.

## 검증 방법

`ProgramManager.DesktopChecks`는 제품의 실제 `MainForm.cs`, `Dialogs.cs`, `Ui.cs`를 사용합니다. .NET 8 검사는 제품과 같은 `SystemAware` 모드, .NET Framework 검사는 제품 매니페스트를 사용합니다. 시험용 프로그램 목록과 버전 정보를 넣고 다음 여덟 화면을 검사합니다.

내 프로그램, 배포 카탈로그, 호스트 관리, 등록/편집, 배포본 등록, 설정, 연결 코드, 설명/이력.

각 런타임에서 실제 250% 환경과 100·125·150·200·250% 크기 모의 검사를 수행합니다. 모의 검사는 해당 프로세스의 글꼴과 컨트롤 크기를 조절하고 작은 창으로 제한하며, Windows 화면 설정은 변경하지 않습니다. 실제 모니터의 배율 변경 또는 다중 모니터 이동 시험과는 다릅니다.

자동 검사는 글자와 표 머리글·행의 높이 및 너비, 도구 모음과 창 경계, 불필요한 가로 스크롤, 하단 버튼 접근성, 빈 검색 결과와 Windows 대상별 버튼 상태를 확인합니다. 화면 이미지는 실제 폼의 `DrawToBitmap` 결과입니다.

## 재현과 증거

```powershell
# 두 런타임의 기능 검사, UI 검사, 양방향 통신 검사와 설치 파일 생성
./tools/build.ps1

# UI 검사만 실행
dotnet run --project tests/ProgramManager.DesktopChecks -c Release -f net8.0-windows -- --layout-check artifacts/layout/net8
./tests/ProgramManager.DesktopChecks/bin/Release/net48/ProgramManager.DesktopChecks.exe --layout-check artifacts/layout/net48
```

UI 결과: `artifacts/layout/net8/layout-report.txt`, `artifacts/layout/net48/layout-report.txt`. 각 런타임의 하위 폴더에 화면 48개가 생성됩니다.

## 최종 결과

- 두 런타임의 UI 검사 통과: 총 96개 화면. 현재 250% 환경에서 제목, 목록/카탈로그, 설정 창과 좁은 창의 배포본 등록 화면을 직접 열어 확인했습니다.
- Release 빌드 경고 0, 오류 0. 기존 카탈로그·인증·전송·설정 보존·바로가기 검사와 두 런타임 간 양방향 통신 검사 통과.
- 최종 설치 파일을 카탈로그에 게시해 내려받고 크기와 SHA-256 비교 통과: Windows 10/11용 51,013,591바이트, Win7용 2,373,658바이트.
- 현재 PC의 기존 설치본을 0.1.0에서 0.1.1로 업데이트했습니다. 설치 종료 코드 0, 설치된 465개 파일의 해시가 최종 게시 파일과 일치합니다.
- 설치본의 `Program Manager 0.1.1` 창 제목과 기본 화면 접근성 트리를 확인했습니다.

빌드 로그: `artifacts/build-0.1.1.log`. 설치 업데이트 증거: `artifacts/upgrade-verification-0.1.1.json`, `artifacts/upgrade-0.1.1.log`. 설치 파일 해시: `artifacts/installers/SHA256SUMS.txt`.

## 확인 범위의 한계

Windows 7 실기기의 설치·실행, 별도 물리 PC 및 서로 다른 배율의 모니터 간 이동은 확인하지 않았습니다. Win7용 빌드는 Windows 10의 .NET Framework 4.8에서 검사했습니다. 화면 자동 검사는 네이티브 마우스로 모든 설치·업데이트 절차를 끝까지 수행한 결과를 의미하지 않습니다.

GitHub 원격 저장소가 없어 원격 CI와 Release 게시는 수행하지 않았습니다.
