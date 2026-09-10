# Program Manager 0.1.0 검증 기록

검증일: 2026-09-10. 실행 환경: Windows 10 Pro 64비트, 10.0.19045, .NET SDK 8.0.425 및 .NET Framework 4.8.

## 통과한 검사

- WinForms 앱 `net8.0-windows` / `net48` Release 빌드: 경고 0, 오류 0.
- 두 런타임에서 카탈로그 게시, 숫자 버전 정렬, 동일 프로그램·버전의 Windows별 설치 파일 분리, 중복 게시 거부.
- 인증서 고정 및 토큰 인증, 잘못된 인증서·토큰 거부, 카탈로그 입력 검증, 경로 탈출 거부.
- 다운로드 크기·SHA-256 검증, 변조 거부, 취소 및 임시 파일 정리, 이전 정상 파일 보존.
- .NET 8 호스트 → .NET Framework 4.8 클라이언트 및 역방향: 목록 조회와 두 Windows용 설치 파일 전송.
- 실제 Windows 바로가기의 실행 대상·인수·작업 폴더 보존, 원본 바로가기 삭제 후 Manager 보관본 유지, 중복 등록 처리.
- 잠긴 설정 파일로 저장 실패를 발생시켜 프로그램 목록·메모리 설정 보존 및 새 임시 바로가기 정리 확인.
- 실제 WinForms 코드로 목록·배포 카탈로그·호스트 관리·등록·게시·설정 창을 렌더해 확인. 창 닫기가 프로세스 종료 대신 트레이 숨김으로 처리됨을 확인.
- 실제 IntraDrop 1.7.0 설치 파일 47,746,581바이트 / 2,482,727바이트를 게시·전송하고 원본 해시와 비교.
- 최종 Program Manager 설치 파일 51,018,206바이트 / 2,372,066바이트를 게시·전송하고 원본 해시와 비교.
- 두 설치 파일을 전용 검사 폴더에 설치: 최신 Windows용 465개 파일, Win7용 13개 파일을 게시 결과와 해시 비교. 설치본 실행, `Program Manager 0.1.0` 제목, 중복 인스턴스 종료 및 제거 확인.

## 재현

```powershell
# 빌드, 두 런타임 검사, 양방향 통신, 두 설치 파일 생성
./tools/build.ps1

# 실제 설치 파일 전송 검사 (실행하거나 설치하지 않음)
dotnet run --project tests/ProgramManager.Checks -c Release -f net8.0 -- --artifacts artifacts/installers/ProgramManager-Setup-0.1.0.exe artifacts/installers/ProgramManager-Setup-0.1.0-win7.exe

# 테스트 데이터로 실제 WinForms 화면 렌더 및 닫기 동작 검사
dotnet run --project tests/ProgramManager.DesktopChecks -c Release -f net8.0-windows -- --render C:/Temp/ProgramManager-Screenshots
```

로컬 증거: `artifacts/build-final.log`, `artifacts/installer-verification.json`, `artifacts/install-*.log`, `artifacts/screenshots/`, `artifacts/installers/SHA256SUMS.txt`.

## 아직 확인하지 못한 범위

Windows 7 실기기 및 별도 물리 PC 간의 방화벽·네트워크 연결은 확인하지 못했습니다. 위 net48 실행 및 양방향 통신 검사는 Windows 10 한 대에서 서로 다른 프로세스로 수행했습니다. Windows 7용은 .NET Framework 4.8 설치가 필요합니다.

네이티브 화면 캡처 도구는 이 환경에서 `SetIsBorderRequired (0x80004002)` 오류가 발생했습니다. 실행 중인 앱의 접근성 트리와 창 제목은 확인했고, 화면 배치는 실제 UI 코드를 사용하는 `DrawToBitmap` 검사로 확인했습니다. 네이티브 마우스 클릭을 통한 전체 설치·업데이트 흐름 검증은 완료하지 않았습니다.

GitHub 원격 저장소가 연결되어 있지 않아 원격 CI 및 Release 게시를 수행하지 않았습니다. IntraDrop 기존 프로젝트는 수정하지 않았습니다.
