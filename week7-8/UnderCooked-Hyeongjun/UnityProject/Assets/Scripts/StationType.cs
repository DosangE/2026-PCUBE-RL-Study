// 그리드 위 상호작용 지점의 종류.
// Counter만 양쪽 구역에서 접근 가능하고, 나머지 4종은 한쪽 구역 전용이다.
public enum StationType
{
    IngredientBox = 0,  // 재료함  (Chef A 구역)
    Pot = 1,            // 냄비    (Chef A 구역)
    PlateStack = 2,     // 그릇함  (Chef B 구역)
    ServingHatch = 3,   // 서빙구  (Chef B 구역)
    Counter = 4         // 중앙 카운터 전달칸 (공용)
}
