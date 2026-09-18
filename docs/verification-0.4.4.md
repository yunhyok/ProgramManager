# Program Manager 0.4.4 검증 기록

호스트 관리의 맨 앞에 있던 **호스트 설정** 버튼을 제거했습니다. 도구 모음은 **저장소 선택**부터 시작하며, 주소·계정·연결 코드의 안내 경로는 **설정 → 호스트 · 배포**로 통일했습니다. README와 설치에 포함되는 도움말에도 같은 경로를 반영했습니다.

현재 Windows에서 .NET 8 및 .NET Framework 4.8 Release 빌드와 코어·데스크톱 검사가 통과했습니다. 실제 배율과 100/125/150/200/250% 모의 배율 검사도 모두 통과했으며 건너뛴 배율은 없습니다. 두 런타임 사이의 호스트·클라이언트 통신 검사도 통과했습니다.

실제 폼을 렌더링한 호스트 관리 화면에서 첫 버튼이 **저장소 선택**이고 중복 버튼이 없는 것을 확인했습니다. 설정 창에는 **호스트 · 배포** 탭과 기존 설정 항목이 표시됩니다. 창 제목은 **Program Manager 0.4.4**입니다. 로컬 화면 자료는 `artifacts/ui-0.4.4/`, 배율별 자료는 `artifacts/layout/`, 전체 빌드 로그는 `artifacts/build-0.4.4.log`에 있습니다.

Windows 7 실기기에서 실행한 결과는 아닙니다.

배포 커밋은 `e1b995130540d2c2b7a4f354d592f579a1d4b03b`입니다. [태그 CI 35288995958](https://github.com/yunhyok/ProgramManager/actions/runs/35288995958)와 main CI 35288995901이 성공했고, [v0.4.4 릴리스](https://github.com/yunhyok/ProgramManager/releases/tag/v0.4.4)에 두 설치 파일과 `SHA256SUMS.txt`가 게시됐습니다. 공개 파일을 내려받아 매니페스트 및 GitHub 자산의 해시·크기와 대조했습니다.

- Windows 10/11: 51,271,213 bytes, SHA-256 `bfe064fe86e9dfa0d18c11c4dec231dc4123725e3c6888c8d5e9f3a6d0f127f5`
- Windows 7/8: 2,638,447 bytes, SHA-256 `f631d8f5b2cdac0570ce00ce434e9a1cc6495564f7063ab2afc2367ca8d0f2ab`

현재 PC에는 공개 Windows 10/11 설치 파일을 기존 자동 업데이트 경로로 적용했습니다. 설치 파일의 제품·버전 검증, 이전 파일 백업, 정상 재시작 및 설치 실행 파일의 제품 버전 `0.4.4+e1b995130540d2c2b7a4f354d592f579a1d4b03b`를 확인했습니다. 프로그램 19개, GitHub 선택 10개, 실행 경로와 연결 설정이 보존됐습니다. 로컬 증거는 `artifacts/self-update-installed-0.4.4.json`에 있습니다.

설치본 창 제목과 프로그램 19개 표시는 Windows 접근성 정보로 확인했습니다. 설치본의 화면 캡처는 Windows 도구의 `SetIsBorderRequired` 인터페이스 오류(0x80004002)로 완료하지 못했습니다. 위의 버튼 제거·설정 화면 시각 검증은 실제 제품 폼을 사용하는 데스크톱 검사의 렌더링 결과입니다.
