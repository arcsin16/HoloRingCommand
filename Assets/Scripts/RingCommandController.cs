using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.WSA.Input;

public class RingCommandController : MonoBehaviour
{
    // リングコマンド制御状態
    private enum RingCommandState
    {
        INACTIVE,
        ACTIVE,
        ANIMATING_FADE_IN,
        ANIMATING_FADE_OUT,
        ANIMATING_ROTATE,
    }

    // リングコマンドに表示するGameObjectのPrefab
    public GameObject[] iconPrefabs;

    // リングコマンドの半径
    public float normalRadius = 0.3f;

    // リングコマンドのフェードイン/アウトの半径変化速度
    public float fadeOutRadiusPerSec = 2.0f;

    // リングコマンドのフェードイン/アウトの角度変化速度
    public float fadeOutAnglePerSec = 180.0f;

    // リングコマンドのフェードイン/アウトにかかる時間
    public float fadeOutTime = 1.0f;

    // 回転アニメーションにかかる時間
    public float rotationTime = 0.2f;

    // 回転を開始するために必要な手の移動量の閾値
    public float rotationThreshold = 0.05f;

    // 非選択状態のアイテムの表示用マテリアル
    public Material iconMaterial;

    // 選択状態のアイテムの表示用マテリアル
    public Material iconMaterialSelected;

    // フェードアウト時の効果音
    public AudioClip fadeOutSound;

    // フェードイン時の効果音
    public AudioClip fadeInSound;

    // 回転時の効果音
    public AudioClip rotateSound;

    // 選択時の効果音
    public AudioClip selectSound;

    // リングコマンドの状態
    private RingCommandState state;

    // リングコマンドに表示するGameObject
    private GameObject[] commandIcons;
    
    // 現在選択されているアイテムのIndex
    private int selectedCommand;

    // 回転アニメーションで使用する、アニメーション後に選択されるアイテムのIndex
    private int nextSelect;
    
    // フェードイン/アウトアニメーションの経過割合（0～1、0:通常状態, 1:フェードアウト状態）
    private float fadeRate;

    // 回転アニメーションの経過割合（0～1、0:回転開始、1:回転完了）
    private float rotateRate;

    // 手の左右方向の移動量(left < 0 < right)
    private float handMoveLeftRight;

    // 前回の手の位置
    private Vector3 lastHandPos;

    void Start()
    {
        // リングコマンドの状態、ゲームオブジェクトを初期化
        commandIcons = new GameObject[iconPrefabs.Length];
        for (int i = 0; i < iconPrefabs.Length; i++)
        {
            commandIcons[i] = Instantiate(iconPrefabs[i]);
            commandIcons[i].SetActive(false);
            commandIcons[i].transform.SetParent(this.transform);
            var renderer = commandIcons[i].GetComponent<MeshRenderer>();
            renderer.material = iconMaterial;
        }

        {
            this.selectedCommand = 0;
            var renderer = this.commandIcons[0].GetComponent<MeshRenderer>();
            renderer.material = iconMaterialSelected;
        }
    }

    private void OnEnable()
    {
        state = RingCommandState.INACTIVE;
        fadeRate = 1.0f;
        InteractionManager.InteractionSourceDetected += InteractionManager_InteractionSourceDetected;
        InteractionManager.InteractionSourceLost     += InteractionManager_InteractionSourceLost;
        InteractionManager.InteractionSourcePressed  += InteractionManager_InteractionSourcePressed;
        InteractionManager.InteractionSourceReleased += InteractionManager_InteractionSourceReleased;
        InteractionManager.InteractionSourceUpdated  += InteractionManager_InteractionSourceUpdated;
    }

    private void OnDisable()
    {
        InteractionManager.InteractionSourceDetected -= InteractionManager_InteractionSourceDetected;
        InteractionManager.InteractionSourceLost     -= InteractionManager_InteractionSourceLost;
        InteractionManager.InteractionSourcePressed  -= InteractionManager_InteractionSourcePressed;
        InteractionManager.InteractionSourceReleased -= InteractionManager_InteractionSourceReleased;
        InteractionManager.InteractionSourceUpdated  -= InteractionManager_InteractionSourceUpdated;
    }

    void Update()
    {
        // テスト用
        // Unity Playerでスペースキー、A、Dキーである程度操作できるように
        if (Input.GetKeyDown(KeyCode.Space))
        {
            switch (state)
            {
                case RingCommandState.ACTIVE:
                case RingCommandState.ANIMATING_FADE_IN:
                    StartFadeOut();
                    break;
                case RingCommandState.INACTIVE:
                case RingCommandState.ANIMATING_FADE_OUT:
                    StartFadeIn();
                    break;
            }
        }
        else if (Input.GetKeyDown(KeyCode.A)) // Left
        {
            MoveHand(-rotationThreshold * 1.1f);
        }
        else if (Input.GetKeyDown(KeyCode.D)) // Right
        {
            MoveHand(rotationThreshold * 1.1f);
        }

        // アニメーション中の場合、アニメーションの経過割合を更新する
        switch (state)
        {
            case RingCommandState.ANIMATING_FADE_IN:
                // フェードインアニメーションのため、フェードの経過割合を更新する
                fadeRate -= Time.deltaTime / fadeOutTime;

                // フェードイン完了
                if (fadeRate < 0.0f)
                {
                    OnFadeInCompleted();
                }
                break;
            case RingCommandState.ANIMATING_FADE_OUT:
                // フェードアウトアニメーションのため、フェードの経過割合を更新する
                fadeRate += Time.deltaTime / fadeOutTime;

                // フェードアウト完了
                if (fadeRate > 1.0f)
                {
                    OnFadeOutCompleted();
                }
                break;

            case RingCommandState.ANIMATING_ROTATE:
                // 回転アニメーションのため、回転の経過割合を更新する
                rotateRate += Time.deltaTime / rotationTime;
                if (rotateRate > 1.0f)
                {
                    OnRotationComplete();
                }
                break;
        }

        // 位置更新
        // まずは半径を計算（フェードイン/アウトを考慮して）
        var radius = normalRadius + fadeRate * fadeOutRadiusPerSec / fadeOutTime;

        // 各アイテムの表示位置を更新する
        for (int i = 0; i < commandIcons.Length; i++)
        {
            // 各アイテムの位置（Index）を計算（回転アニメーションを考慮して）
            // 選択中のアイテムを起点とした位置（i - this.selectedCommand)を0～this.commandIcons.Lengthに収まるようにしつつ
            // アニメーションのためアニメーション前(this.selectedCommand)、後(this.nextSelect)にrotateRateで重みづけして
            // 計算しています。
            float idx = (i - ((1.0f - rotateRate) * this.selectedCommand + rotateRate * this.nextSelect) + this.commandIcons.Length) % this.commandIcons.Length;
            // そこから実際の角度に変換して
            var angle = GetCommandAngle(idx) + fadeRate * fadeOutAnglePerSec / fadeOutTime;
            var rad = angle * Mathf.PI / 180.0f;
            // x,y 座標を求める（親（RingCommandController）に対する相対座標）
            commandIcons[i].transform.localPosition = new Vector3(
                radius * Mathf.Cos(rad),
                radius * Mathf.Sin(rad),
                0.0f);
        }
    }

    private void InteractionManager_InteractionSourceUpdated(InteractionSourceUpdatedEventArgs obj)
    {
        // 手の位置の更新のコールバック
        // 手の移動、ジェスチャーの処理はここでやるとよい。
        if (state == RingCommandState.ACTIVE)
        {
            // 手の位置を取得
            Vector3 handPos;
            obj.state.sourcePose.TryGetPosition(out handPos);

            if (lastHandPos != Vector3.zero)
            {
                // 前回の手の位置からの移動量を計算
                var move = handPos - lastHandPos;

                // カメラ座標系を基準とした左右移動に変換する
                var right = Vector3.Dot(move, Camera.main.transform.right);

                // 移動量を蓄積して、選択状態の変更の判定を行う
                MoveHand(right);
            }

            lastHandPos = handPos;
        }
    }

    private void InteractionManager_InteractionSourceDetected(InteractionSourceDetectedEventArgs obj)
    {
        // 手の認識時のコールバック
        if (state == RingCommandState.INACTIVE)
        {
            StartFadeIn();
        }
    }

    private void InteractionManager_InteractionSourceLost(InteractionSourceLostEventArgs obj)
    {
        // 手のロスト時のコールバック
        if (state != RingCommandState.INACTIVE)
        {
            StartFadeOut();
        }
    }

    private void InteractionManager_InteractionSourcePressed(InteractionSourcePressedEventArgs obj)
    {
        // Air-Tapの押下時のコールバック処理
        if (state == RingCommandState.ACTIVE)
        {
            // 選択しているアイテムを生成する

            // 選択時の効果音
            if(selectSound != null)
            {
                AudioSource.PlayClipAtPoint(selectSound, this.transform.position);
            }

            if (this.selectedCommand != -1)
            {
                // リングコマンドの表示位置に選択したアイテムの複製を生成
                var model = Instantiate(this.commandIcons[this.selectedCommand]);
                model.transform.position = this.transform.position;
                var rigidbody = model.AddComponent<Rigidbody>();
                rigidbody.useGravity = true;
            }
        }
    }

    private void InteractionManager_InteractionSourceReleased(InteractionSourceReleasedEventArgs obj)
    {
        // Air-Tapのリリース時のコールバック処理
        if (state == RingCommandState.ACTIVE)
        {
            // リングコマンドを消す
            // 選択しているアイテムの生成とタイミングを少し開けたかったので、
            // Pressedで生成、Releasedでコマンド消滅としてみた。
            StartFadeOut();
        }
    }

    private void SelectIcon(int index)
    {
        // リングコマンドの選択状態を更新する
        if (this.selectedCommand != -1)
        {
            // 選択されていたアイテムの見た目を非選択状態に戻す
            var renderer = this.commandIcons[this.selectedCommand].GetComponent<MeshRenderer>();
            renderer.material = iconMaterial;
        }

        // 選択状態を更新する。
        this.selectedCommand = (index + this.commandIcons.Length) % this.commandIcons.Length;
        Debug.Log("SelectIcon " + this.selectedCommand);
        {
            // 選択されたアイテムの見た目を選択状態に設定する
            var renderer = this.commandIcons[this.selectedCommand].GetComponent<MeshRenderer>();
            renderer.material = iconMaterialSelected;
        }
    }

    private float GetCommandAngle(float index)
    {
        // リングコマンドの各アイテムのIndexに応じた表示位置の角度を取得する
        return -90 + index * 360.0f / commandIcons.Length;
    }

    private void StartFadeIn()
    {
        // フェードアウト処理を開始する
        // ステータスを更新し、アニメーション系のパラメータを初期化する
        // アニメーション系のパラメータの更新はUpdateで行う
        state = RingCommandState.ANIMATING_FADE_IN;
        for (int i = 0; i < commandIcons.Length; i++)
        {
            commandIcons[i].SetActive(true);
        }
        // リングコマンドの表示位置をフェードイン開始時のカメラの1.5m先に設定する。
        // カメラに追従するのもうざいかもなので、ワールドロック的にしておく。
        this.transform.position = Camera.main.transform.position + Camera.main.transform.forward * 1.5f;
        this.transform.LookAt(Camera.main.transform.position + Camera.main.transform.forward * 2.0f, Vector3.up);
        if (fadeInSound != null)
        {
            AudioSource.PlayClipAtPoint(fadeInSound, this.transform.position);
        }
    }

    private void OnFadeInCompleted()
    {
        fadeRate = 0.0f;
        state = RingCommandState.ACTIVE;
        handMoveLeftRight = 0.0f;
        lastHandPos = Vector3.zero;
    }

    private void StartFadeOut()
    {
        // フェードアウト処理を開始する
        // ステータスを更新するのみで、アニメーション系のパラメータの更新はUpdateで行う
        if(fadeOutSound != null)
        {
            AudioSource.PlayClipAtPoint(fadeOutSound, this.transform.position);
        }
        state = RingCommandState.ANIMATING_FADE_OUT;
    }

    private void OnFadeOutCompleted()
    {
        // フェードアウトが完了したので、ステートを更新して、リングコマンドのアイテムを非活性化する
        fadeRate = 1.0f;
        state = RingCommandState.INACTIVE;
        for (int i = 0; i < commandIcons.Length; i++)
        {
            commandIcons[i].SetActive(false);
        }
    }

    private void StartRotation(int direction)
    {
        // 回転処理を開始する
        // ステートを更新して、回転アニメーション関連のパラメータを初期設定する
        state = RingCommandState.ANIMATING_ROTATE;
        rotateRate = 0.0f;
        this.nextSelect = (this.selectedCommand + direction + this.commandIcons.Length) % this.commandIcons.Length;

        if (rotateSound != null)
        {
            AudioSource.PlayClipAtPoint(rotateSound, this.transform.position);
        }
    }

    private void OnRotationComplete()
    {
        // 回転処理完了時に選択状態を更新
        SelectIcon(this.nextSelect);
        if (state == RingCommandState.ANIMATING_ROTATE)
        {
            // ステートを更新して、アニメーション用のrotateRateをリセットする。
            state = RingCommandState.ACTIVE;
            rotateRate = 0.0f;
        }
    }

    private void MoveHand(float dx)
    {
        // 手の移動量を積算して、閾値以上になったら選択状態を更新する。
        handMoveLeftRight += dx;
        Debug.Log("MoveHand " + handMoveLeftRight);

        if (handMoveLeftRight > rotationThreshold)
        {
            StartRotation(1);
            handMoveLeftRight = 0.0f;
        }
        else if (handMoveLeftRight < -rotationThreshold)
        {
            StartRotation(-1);
            handMoveLeftRight = 0.0f;
        }
    }
}
