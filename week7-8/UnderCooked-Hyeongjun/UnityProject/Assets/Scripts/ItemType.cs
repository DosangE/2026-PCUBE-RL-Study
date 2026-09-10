// 셰프가 손에 들 수 있는 아이템 종류.
// 관측의 one-hot(4) 인덱스와 enum 값이 그대로 일치한다. 순서를 바꾸면 관측이 깨진다.
public enum ItemType
{
    None = 0,
    Ingredient = 1,
    EmptyPlate = 2,
    CookedSoup = 3
}
